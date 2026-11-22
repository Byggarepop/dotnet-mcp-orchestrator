using System.Text.Json;
using Jint;
using Jint.Native;
using Jint.Runtime.Interop;
using Jint.Runtime;

namespace McpOrchestrator.Orchestration.CodeMode;

/// <summary>Forwards one tool call to a capability and returns the <c>route</c> tool's JSON response.</summary>
internal delegate Task<string> RouteInvoker(
    string capability, string tool, JsonElement arguments, CancellationToken cancellationToken);

/// <summary>Hard limits for one <c>execute</c> run.</summary>
/// <param name="Timeout">Wall-clock limit for the whole run, route calls included.</param>
/// <param name="MaxRouteCalls">How many downstream calls one script may make.</param>
/// <param name="MemoryBytes">Memory the interpreter may allocate for script values.</param>
/// <param name="MaxStatements">Statement budget — stops runaway loops before the timeout.</param>
internal sealed record CodeModeLimits(TimeSpan Timeout, int MaxRouteCalls, long MemoryBytes, int MaxStatements)
{
    public static CodeModeLimits Default { get; } = new(
        Timeout: TimeSpan.FromSeconds(30),
        MaxRouteCalls: 50,
        MemoryBytes: 64L * 1024 * 1024,
        MaxStatements: 10_000_000);
}

/// <summary>
/// Runs a model-written JavaScript snippet in a sandbox where the only capability is
/// <c>route(capability, tool, args)</c>. Several downstream calls and the filtering between them
/// happen here, so only the snippet's return value reaches the model — not every intermediate
/// result, and not one round trip per call.
/// <para>
/// Sandbox: Jint (a JavaScript interpreter written in C#) with no CLR access, no modules, no I/O,
/// strict mode, and limits on time, statements, memory, recursion and route calls. The route bridge
/// is a plain <see cref="ClrFunction"/> (no reflection-based interop).
/// </para>
/// <para>
/// <c>route</c> is synchronous under the hood: the host call blocks the interpreter thread until the
/// downstream answers. <c>await route(...)</c> and <c>Promise.all</c> still work as written — awaiting
/// a plain value is allowed — but calls run one after another, not in parallel.
/// </para>
/// </summary>
internal static class CodeModeRunner
{
    /// <summary>
    /// Script-side helpers. <c>route</c> unwraps the orchestrator's <c>route</c> response into the
    /// data itself: <c>structured</c> (minus the SDK's <c>{"result": ...}</c> wrapper for non-object
    /// results), else <c>text</c> parsed as JSON, else the raw text. A failed call throws an Error
    /// whose <c>details</c> carry the full response (error text, inputSchema, availableTools …).
    /// </summary>
    private const string Prelude = """
        "use strict";
        const console = Object.freeze({ log() {}, info() {}, warn() {}, error() {} });
        function route(capability, tool, args) {
          const r = JSON.parse(__route(String(capability), String(tool), JSON.stringify(args === undefined ? {} : args)));
          if (r.error !== undefined || r.isError === true) {
            const e = new Error(r.error || r.text || ("route " + capability + "/" + tool + " failed"));
            e.details = r;
            throw e;
          }
          if (r.structured !== undefined && r.structured !== null) {
            const s = r.structured;
            const keys = (s && typeof s === "object" && !Array.isArray(s)) ? Object.keys(s) : [];
            return keys.length === 1 && keys[0] === "result" ? s.result : s;
          }
          if (typeof r.text === "string") {
            try { return JSON.parse(r.text); } catch (_) { return r.text; }
          }
          return null;
        }
        """;

    /// <summary>Runs <paramref name="code"/> (the body of an async function) and returns the execute response JSON.</summary>
    public static async Task<string> RunAsync(
        string code, RouteInvoker route, CodeModeLimits limits, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(limits.Timeout);

        var calls = 0;
        try
        {
            // The interpreter is synchronous; keep it (and the blocking route bridge) off the caller's thread.
            var json = await Task.Run(() => Evaluate(code, route, limits, deadline.Token, () => ++calls), deadline.Token);
            using var doc = JsonDocument.Parse(json);
            return OrchestratorJson.Serialize(new ExecuteView(doc.RootElement.Clone(), Error: null, calls));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"Script exceeded the {limits.Timeout.TotalSeconds:0} s time limit.", calls);
        }
        catch (ExecutionCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return Failure($"Script exceeded the {limits.Timeout.TotalSeconds:0} s time limit.", calls);
        }
        catch (TimeoutException)
        {
            return Failure($"Script exceeded the {limits.Timeout.TotalSeconds:0} s time limit.", calls);
        }
        catch (StatementsCountOverflowException)
        {
            return Failure($"Script exceeded the statement limit ({limits.MaxStatements:N0}) — likely an endless loop.", calls);
        }
        catch (MemoryLimitExceededException)
        {
            return Failure($"Script exceeded the memory limit ({limits.MemoryBytes / (1024 * 1024)} MB).", calls);
        }
        catch (RecursionDepthOverflowException)
        {
            return Failure("Script exceeded the recursion limit.", calls);
        }
        catch (PromiseRejectedException ex)
        {
            return Failure($"Uncaught error: {Describe(ex.RejectedValue)}", calls);
        }
        catch (JavaScriptException ex)
        {
            return Failure($"Uncaught error: {ex.Message}", calls);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Syntax errors and anything else the interpreter reports.
            return Failure($"Script failed: {ex.Message}", calls);
        }
    }

    private static string Evaluate(
        string code, RouteInvoker route, CodeModeLimits limits, CancellationToken token, Func<int> countCall)
    {
        var engine = new Engine(options => options
            .Strict()
            .TimeoutInterval(limits.Timeout)
            .MaxStatements(limits.MaxStatements)
            .LimitMemory(limits.MemoryBytes)
            .LimitRecursion(64)
            .CancellationToken(token));

        engine.SetValue("__route", new ClrFunction(engine, "__route", (_, args) =>
        {
            var n = countCall();
            if (n > limits.MaxRouteCalls)
            {
                throw new JavaScriptException(
                    engine.Intrinsics.Error,
                    $"route call limit ({limits.MaxRouteCalls}) exceeded");
            }

            using var doc = JsonDocument.Parse(args.At(2).AsString());
            var response = route(args.At(0).AsString(), args.At(1).AsString(), doc.RootElement.Clone(), token)
                .GetAwaiter().GetResult();
            return new JsString(response);
        }));

        engine.Execute(Prelude);

        var value = engine.Evaluate("(async () => {\n" + code + "\n})()").UnwrapIfPromise();
        engine.SetValue("__value", value);
        var json = engine.Evaluate("JSON.stringify(__value === undefined ? null : __value)");
        return json.IsString() ? json.AsString() : "null";
    }

    private static string Describe(JsValue value) =>
        value is Jint.Native.Object.ObjectInstance obj && obj.Get("message") is { } message && message.IsString()
            ? message.AsString()
            : value.ToString();

    private static string Failure(string message, int calls) =>
        OrchestratorJson.Serialize(new ExecuteView(Result: null, message, calls));
}
