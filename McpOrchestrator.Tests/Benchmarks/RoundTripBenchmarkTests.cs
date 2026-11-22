using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Profiling;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace McpOrchestrator.Tests.Benchmarks;

/// <summary>
/// Token benchmark for the agent's round trips through the orchestrator. It answers one
/// question with measured numbers instead of opinions: <em>where do the tokens go when an agent
/// runs list_capabilities → discover_tools → route, and how much would the candidate
/// improvements save?</em>
/// <para>
/// Every payload is REAL: produced by the orchestrator's own tool methods against the demo
/// downstream (personas <c>jira</c> and <c>bench</c>) and counted with the same cl100k_base
/// tokenizer the <c>profile</c> command uses. Only the code-mode <c>execute</c> tool is
/// simulated: its final result is built from real route results, and its script is a
/// realistic hand-written one.
/// </para>
/// <para>
/// The host model: the model is stateless, so every request re-sends the whole conversation.
/// Request <c>i</c> sends floor + prior context + prompt + everything produced so far. The part added since the
/// previous request is "uncached" (cache write); the rest is a cache read. Relative prices
/// (cache write 1.25, cache read 0.10, output 5.0 × base input) follow Anthropic's published
/// ratios and are parameters, not facts about every host. The final answer the model writes
/// is the same for every strategy and is left out.
/// </para>
/// Run only these: <c>dotnet test --filter Category=Benchmark --logger "console;verbosity=detailed"</c>.
/// </summary>
[Trait("Category", "Benchmark")]
public sealed class RoundTripBenchmarkTests(ITestOutputHelper output)
{
    private static readonly ILogger<OrchestratorTool> Log = new NullLogger<OrchestratorTool>();
    private static readonly ITokenCounter Counter = new Cl100kTokenCounter();

    /// <summary>Context a real session already carries before the task (system prompt, files, chat).</summary>
    private static readonly int[] PriorContextLevels = [0, 20_000];

    [Fact]
    public async Task Round_trip_strategies_token_report()
    {
        var (catalog, conn) = Demo.Pair(
            Demo.Capability("jira", "jira"),
            Demo.Capability("bench", "bench"));
        await using var owned = conn;
        var ct = CancellationToken.None;

        var floor = OrchestratorSurface.MeasureFloor(Counter).FloorTokens;
        var listCaps = await OrchestratorTool.ListCapabilities(catalog, Log);

        var tasks = new List<BenchTask>
        {
            await SingleLookupAsync(catalog, conn, listCaps, ct),
            await SmallFanOutAsync(catalog, conn, listCaps, ct),
            await BigResultFilterAsync(catalog, conn, listCaps, ct),
        };

        var report = new StringBuilder();
        report.AppendLine("# Round-trip token benchmark");
        report.AppendLine();
        report.AppendLine($"Tokenizer: {Counter.Name} (±{Counter.CrossModelTolerancePct}% across models). " +
                          $"Orchestrator floor (meta-tool definitions): {floor} tokens.");
        report.AppendLine("Cost units = uncached×1.25 + cached×0.10 + output×5.0 (relative to base input price).");
        report.AppendLine();

        report.AppendLine("## Payload anatomy (per route result)");
        report.AppendLine();
        report.AppendLine("| Call | Total | text | structured | arguments echo | other |");
        report.AppendLine("|---|---:|---:|---:|---:|---:|");
        foreach (var (label, json) in tasks.SelectMany(t => t.RouteSamples))
        {
            var a = Anatomy(json);
            report.AppendLine($"| {label} | {a.Total} | {a.Text} | {a.Structured} | {a.Arguments} | {a.Total - a.Text - a.Structured - a.Arguments} |");
        }
        report.AppendLine();

        foreach (var prior in PriorContextLevels)
        {
            report.AppendLine($"## Prior context in session: {prior:N0} tokens");
            report.AppendLine();
            foreach (var task in tasks)
            {
                report.AppendLine($"### {task.Name}");
                report.AppendLine();
                report.AppendLine("| Strategy | Round trips | Input total | Uncached | Cached | Output | Cost units | vs current |");
                report.AppendLine("|---|---:|---:|---:|---:|---:|---:|---:|");

                var results = task.Strategies
                    .Select(s => ConversationModel.Run(s, Counter, floor, prior, Counter.Count(task.Prompt)))
                    .ToList();
                var baseline = results[0].CostUnits;
                foreach (var r in results)
                {
                    var delta = baseline == 0 ? 0 : (r.CostUnits - baseline) / baseline;
                    report.AppendLine(
                        $"| {r.Name} | {r.RoundTrips} | {r.InputTotal:N0} | {r.InputUncached:N0} | {r.InputCached:N0} | " +
                        $"{r.Output:N0} | {r.CostUnits:N0} | {delta:+0%;-0%;0%} |");
                }
                report.AppendLine();

                // Sanity: the model is internally consistent and every strategy did real work.
                foreach (var r in results)
                {
                    Assert.Equal(r.InputTotal, r.InputUncached + r.InputCached);
                    Assert.True(r.RoundTrips >= 2);
                }
            }
        }

        var text = report.ToString();
        output.WriteLine(text);

        var path = Path.Combine(AppContext.BaseDirectory, "round-trip-benchmark.md");
        await File.WriteAllTextAsync(path, text, ct);
        output.WriteLine($"Report written to {path}");
    }

    /// <summary>
    /// The orchestrator serializes with System.Text.Json's default encoder, which escapes ' + &lt; &gt;
    /// &amp; and every non-ASCII character (å → å). Swedish text in summaries, Jira issues or
    /// database rows pays for that on every call. Measures the overhead against relaxed escaping.
    /// </summary>
    [Fact]
    public void Escaping_overhead_report()
    {
        var samples = new (string Label, string Text)[]
        {
            ("English", "Pick failed: location blocked during replenishment. Contact the store manager."),
            ("Swedish", "Plockning misslyckades: lagerplatsen är blockerad för påfyllnad. Kontakta förrådsansvarig."),
            ("Quotes/apostrophes", "Use 'get_issue' with {\"issueKey\":\"PROJ-1\"} — don't pass a sentence."),
        };

        // Same naming/null rules as OrchestratorJsonContext, only the encoder differs.
        var relaxed = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            TypeInfoResolver = OrchestratorJsonContext.Default,
        };

        var report = new StringBuilder();
        report.AppendLine("| Sample | Default encoder | Relaxed | Overhead |");
        report.AppendLine("|---|---:|---:|---:|");
        foreach (var (label, text) in samples)
        {
            var view = new RouteView { Capability = "x", Tool = "y", Text = text };
            var current = Counter.Count(OrchestratorJson.Serialize(view));
            var better = Counter.Count(JsonSerializer.Serialize(view, relaxed));
            report.AppendLine($"| {label} | {current} | {better} | {(double)(current - better) / better:+0%;-0%;0%} |");
            Assert.True(current >= better); // relaxed escaping never costs more
        }

        output.WriteLine(report.ToString());
    }

    // ----- tasks -----

    /// <summary>One lookup: "what is the status of PROJ-1?" — the case where code mode should NOT help.</summary>
    private static async Task<BenchTask> SingleLookupAsync(
        ICapabilityCatalog catalog, IDownstreamConnectionManager conn, string listCaps, CancellationToken ct)
    {
        var discover = await OrchestratorTool.DiscoverTools(conn, catalog, Log, "jira", ct);
        var args = Args(new JsonObject { ["issueKey"] = "PROJ-1" });
        var route = await RouteAsync(catalog, conn, "jira", "get_issue", args, ct);

        const string script = "return await route('jira','get_issue',{issueKey:'PROJ-1'});";

        return new BenchTask(
            "A. Single lookup (jira get_issue)",
            "What is the status of PROJ-1?",
            [("jira get_issue", route)],
            [
                new Strategy("1. Current: list → discover → route", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "jira" }), discover),
                    MakeStep(RouteCall("jira", "get_issue", args), route),
                ]),
                new Strategy("2. Skip discover: list → route", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(RouteCall("jira", "get_issue", args), route),
                ]),
                new Strategy("3. Skip discover + compact result", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(RouteCall("jira", "get_issue", args), Compact(route)),
                ]),
                new Strategy("4. Code mode: list → discover → execute", ExecuteToolTokens(),
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "jira" }), discover),
                    MakeStep(ExecuteCall(script), ExecuteResult(StructuredOf(route))),
                ]),
                new Strategy("5. Code mode, signatures in description: execute", ExecuteToolTokens(withSignatures: true),
                [
                    MakeStep(ExecuteCall(script), ExecuteResult(StructuredOf(route))),
                ]),
            ]);
    }

    /// <summary>Small fan-out: "list all issues and the status of each" — search + 3 lookups, small payloads.</summary>
    private static async Task<BenchTask> SmallFanOutAsync(
        ICapabilityCatalog catalog, IDownstreamConnectionManager conn, string listCaps, CancellationToken ct)
    {
        var discover = await OrchestratorTool.DiscoverTools(conn, catalog, Log, "jira", ct);
        var searchArgs = Args(new JsonObject { ["query"] = "" });
        var search = await RouteAsync(catalog, conn, "jira", "search_issues", searchArgs, ct);

        var keys = Rows(StructuredOf(search)).Select(r => r["key"]!.GetValue<string>()).ToList();
        var lookups = new List<(JsonElement Args, string Json)>();
        foreach (var key in keys)
        {
            var a = Args(new JsonObject { ["issueKey"] = key });
            lookups.Add((a, await RouteAsync(catalog, conn, "jira", "get_issue", a, ct)));
        }

        var final = new JsonObject
        {
            ["issues"] = new JsonArray(lookups.Select(l => (JsonNode?)StructuredOf(l.Json)).ToArray()),
        };
        const string script =
            "const found = await route('jira','search_issues',{query:''});\n" +
            "const issues = await Promise.all(found.map(i => route('jira','get_issue',{issueKey:i.key})));\n" +
            "return { issues };";

        List<Step> Routed(Func<string, string> shape) =>
            [
                MakeStep(RouteCall("jira", "search_issues", searchArgs), shape(search)),
                .. lookups.Select(l => MakeStep(RouteCall("jira", "get_issue", l.Args), shape(l.Json))),
            ];

        return new BenchTask(
            $"B. Small fan-out (search + {keys.Count} lookups)",
            "List all Jira issues and the status of each one.",
            [("jira search_issues", search)],
            [
                new Strategy("1. Current: list → discover → route×N", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "jira" }), discover),
                    .. Routed(s => s),
                ]),
                new Strategy("2. Skip discover", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    .. Routed(s => s),
                ]),
                new Strategy("3. Skip discover + compact result", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    .. Routed(Compact),
                ]),
                new Strategy("4. Code mode: list → discover → execute", ExecuteToolTokens(),
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "jira" }), discover),
                    MakeStep(ExecuteCall(script), ExecuteResult(final)),
                ]),
                new Strategy("5. Code mode, signatures in description", ExecuteToolTokens(withSignatures: true),
                [
                    MakeStep(ExecuteCall(script), ExecuteResult(final)),
                ]),
            ]);
    }

    /// <summary>
    /// Big result, small answer: "which orders are in error, and what happened to them?" — 150 rows
    /// fetched, 3 kept, 3 detail lookups. The astro-schema-style case code mode is built for.
    /// </summary>
    private static async Task<BenchTask> BigResultFilterAsync(
        ICapabilityCatalog catalog, IDownstreamConnectionManager conn, string listCaps, CancellationToken ct)
    {
        var discover = await OrchestratorTool.DiscoverTools(conn, catalog, Log, "bench", ct);
        var queryArgs = Args(new JsonObject { ["count"] = 150 });
        var query = await RouteAsync(catalog, conn, "bench", "query_orders", queryArgs, ct);

        var errorIds = Rows(StructuredOf(query))
            .Where(r => r["status"]!.GetValue<string>() == "ERR")
            .Select(r => r["orderId"]!.GetValue<string>())
            .ToList();
        var details = new List<(JsonElement Args, string Json)>();
        foreach (var id in errorIds)
        {
            var a = Args(new JsonObject { ["orderId"] = id });
            details.Add((a, await RouteAsync(catalog, conn, "bench", "get_order", a, ct)));
        }

        var final = new JsonObject
        {
            ["errorCount"] = errorIds.Count,
            ["orders"] = new JsonArray(details.Select(d => (JsonNode?)StructuredOf(d.Json)).ToArray()),
        };
        const string script =
            "const rows = await route('bench','query_orders',{count:150});\n" +
            "const errs = rows.filter(r => r.status === 'ERR');\n" +
            "const orders = await Promise.all(errs.map(r => route('bench','get_order',{orderId:r.orderId})));\n" +
            "return { errorCount: errs.length, orders };";

        List<Step> Routed(Func<string, string> shape) =>
            [
                MakeStep(RouteCall("bench", "query_orders", queryArgs), shape(query)),
                .. details.Select(d => MakeStep(RouteCall("bench", "get_order", d.Args), shape(d.Json))),
            ];

        return new BenchTask(
            $"C. Big result, small answer (150 rows → {errorIds.Count} errors + details)",
            "Which warehouse orders are in error, and what happened to each of them?",
            [("bench query_orders(150)", query), ("bench get_order", details[0].Json)],
            [
                new Strategy("1. Current: list → discover → route×N", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "bench" }), discover),
                    .. Routed(s => s),
                ]),
                new Strategy("2. Skip discover", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    .. Routed(s => s),
                ]),
                new Strategy("3. Skip discover + compact result", 0,
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    .. Routed(Compact),
                ]),
                new Strategy("4. Code mode: list → discover → execute", ExecuteToolTokens(),
                [
                    MakeStep(Call("list_capabilities", new JsonObject()), listCaps),
                    MakeStep(Call("discover_tools", new JsonObject { ["capability"] = "bench" }), discover),
                    MakeStep(ExecuteCall(script), ExecuteResult(final)),
                ]),
                new Strategy("5. Code mode, signatures in description", ExecuteToolTokens(withSignatures: true),
                [
                    MakeStep(ExecuteCall(script), ExecuteResult(final)),
                ]),
            ]);
    }

    // ----- payload helpers -----

    private static Task<string> RouteAsync(
        ICapabilityCatalog catalog, IDownstreamConnectionManager conn,
        string capability, string tool, JsonElement args, CancellationToken ct) =>
        OrchestratorTool.Route(conn, catalog, Log, capability, tool, args, ct);

    private static JsonElement Args(JsonObject obj)
    {
        using var doc = JsonDocument.Parse(obj.ToJsonString());
        return doc.RootElement.Clone();
    }

    private static Step MakeStep(string modelOutput, string toolResult) => new(modelOutput, toolResult);

    /// <summary>What the model emits to call a tool: its name plus the argument object.</summary>
    private static string Call(string tool, JsonObject input) =>
        new JsonObject { ["name"] = tool, ["input"] = input }.ToJsonString();

    private static string RouteCall(string capability, string tool, JsonElement args) =>
        Call("route", new JsonObject
        {
            ["capability"] = capability,
            ["tool"] = tool,
            ["arguments"] = JsonNode.Parse(args.GetRawText()),
        });

    private static string ExecuteCall(string script) =>
        Call("execute", new JsonObject { ["code"] = script });

    private static string ExecuteResult(JsonNode? value) =>
        new JsonObject { ["result"] = value?.DeepClone() }.ToJsonString();

    /// <summary>
    /// The candidate "compact" route result: drop the echoed arguments (the model just sent them),
    /// drop <c>text</c> when <c>structured</c> carries the same data, and when there is no
    /// structured content but the text is JSON, embed it as JSON instead of an escaped string
    /// (every <c>\"</c> inside a JSON string costs tokens).
    /// </summary>
    internal static string Compact(string routeJson)
    {
        var node = JsonNode.Parse(routeJson)!.AsObject();
        node.Remove("arguments");
        if (node["structured"] is not null)
        {
            node.Remove("text");
        }
        else if (node["text"]?.GetValue<string>() is { } text && LooksLikeJson(text))
        {
            try
            {
                node["structured"] = JsonNode.Parse(text);
                node.Remove("text");
            }
            catch (JsonException)
            {
                // Not JSON after all — keep the text as is.
            }
        }
        return node.ToJsonString();
    }

    private static bool LooksLikeJson(string text)
    {
        var t = text.TrimStart();
        return t.StartsWith('{') || t.StartsWith('[');
    }

    /// <summary>The data a route returned: <c>structured</c> if present, else <c>text</c> parsed as JSON.</summary>
    private static JsonNode? StructuredOf(string routeJson)
    {
        var node = JsonNode.Parse(routeJson)!.AsObject();
        if (node["structured"] is JsonNode structured)
        {
            return structured.DeepClone();
        }

        var text = node["text"]?.GetValue<string>();
        try
        {
            return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
        }
        catch (JsonException)
        {
            return JsonValue.Create(text);
        }
    }

    /// <summary>The row array inside a result — bare, or wrapped by the SDK (e.g. <c>{"result":[...]}</c>).</summary>
    private static IEnumerable<JsonObject> Rows(JsonNode? node) => node switch
    {
        JsonArray array => array.OfType<JsonObject>(),
        JsonObject obj => obj.Select(p => p.Value).OfType<JsonArray>().FirstOrDefault()?.OfType<JsonObject>()
                          ?? throw new InvalidOperationException($"No row array in result: {obj.ToJsonString()}"),
        _ => throw new InvalidOperationException($"Unexpected result shape: {node?.ToJsonString()}"),
    };

    /// <summary>Tokens per field of one serialized route result.</summary>
    private static (int Total, int Text, int Structured, int Arguments) Anatomy(string routeJson)
    {
        var node = JsonNode.Parse(routeJson)!.AsObject();
        int Field(string name) => node[name] is JsonNode n ? Counter.Count($"\"{name}\":{n.ToJsonString()}") : 0;
        return (Counter.Count(routeJson), Field("text"), Field("structured"), Field("arguments"));
    }

    /// <summary>
    /// Definition tokens a hypothetical <c>execute</c> tool adds to the floor on every request.
    /// With signatures, the compact downstream signatures ride along so no discovery is needed.
    /// </summary>
    private static int ExecuteToolTokens(bool withSignatures = false)
    {
        var description =
            "Run a short JavaScript snippet that calls downstream tools via " +
            "await route(capability, tool, args) and returns only the final value. " +
            "Use it when a task needs 3+ calls in a row or a large result must be filtered; " +
            "for a single call use 'route' directly.";
        if (withSignatures)
        {
            description +=
                "\nAvailable: jira.get_issue({issueKey}), jira.search_issues({query}), " +
                "bench.query_orders({count?}), bench.get_order({orderId}).";
        }

        var definition = new JsonObject
        {
            ["name"] = "execute",
            ["description"] = description,
            ["inputSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["code"] = new JsonObject { ["type"] = "string", ["description"] = "JavaScript to run." },
                },
                ["required"] = new JsonArray("code"),
            },
        };
        return Counter.Count(definition.ToJsonString());
    }
}

// ----- the conversation model -----

/// <summary>One agent step: what the model emitted (the tool call) and what came back.</summary>
internal sealed record Step(string ModelOutput, string ToolResult);

/// <summary>A strategy for a task: its steps and any extra tool-definition tokens it adds to the floor.</summary>
internal sealed record Strategy(string Name, int ExtraFloorTokens, IReadOnlyList<Step> Steps);

/// <summary>A benchmark task, its strategies (the first is the baseline) and route samples for the anatomy table.</summary>
internal sealed record BenchTask(
    string Name,
    string Prompt,
    IReadOnlyList<(string Label, string Json)> RouteSamples,
    IReadOnlyList<Strategy> Strategies);

/// <summary>Token totals for one strategy run.</summary>
internal sealed record ScenarioResult(
    string Name, int RoundTrips, long InputTotal, long InputUncached, long InputCached, long Output)
{
    public double CostUnits =>
        InputUncached * ConversationModel.CacheWrite +
        InputCached * ConversationModel.CacheRead +
        Output * ConversationModel.OutputPrice;
}

/// <summary>
/// Replays a strategy the way a host talks to a stateless model: one request per tool call plus
/// one final request, each re-sending the full context. What was added since the previous request
/// is uncached; everything before it is a cache read.
/// </summary>
internal static class ConversationModel
{
    public const double CacheWrite = 1.25;
    public const double CacheRead = 0.10;
    public const double OutputPrice = 5.0;

    /// <param name="priorContext">
    /// Tokens the session already carries. When non-zero the session is warm: floor and prior
    /// context are cache reads on the first request, and only the new prompt is uncached.
    /// </param>
    public static ScenarioResult Run(
        Strategy strategy, ITokenCounter counter, int floor, int priorContext, int promptTokens)
    {
        long context = floor + strategy.ExtraFloorTokens + priorContext + promptTokens;
        long newSinceLast = priorContext > 0 ? promptTokens : context;
        long inputTotal = 0, uncached = 0, cached = 0, output = 0;

        for (var i = 0; i <= strategy.Steps.Count; i++)
        {
            inputTotal += context;
            uncached += newSinceLast;
            cached += context - newSinceLast;

            if (i == strategy.Steps.Count)
            {
                break; // final request: the model answers; its text is equal across strategies
            }

            var step = strategy.Steps[i];
            var emitted = counter.Count(step.ModelOutput);
            var returned = counter.Count(step.ToolResult);
            output += emitted;
            context += emitted + returned;
            newSinceLast = emitted + returned;
        }

        return new ScenarioResult(strategy.Name, strategy.Steps.Count + 1, inputTotal, uncached, cached, output);
    }
}
