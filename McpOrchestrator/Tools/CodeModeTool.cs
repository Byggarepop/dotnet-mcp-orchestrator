using System.ComponentModel;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.CodeMode;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Tools;

/// <summary>
/// Experimental code mode: one <c>execute</c> tool that runs a JavaScript snippet calling
/// <c>route</c> as a function, so a chain of downstream calls costs one round trip and only the
/// final value reaches the model. Registered only when <see cref="EnvironmentVariable"/> is set —
/// otherwise it adds nothing to the tool list (and nothing to the context floor).
/// </summary>
[McpServerToolType]
public sealed class CodeModeTool
{
    /// <summary>Set to <c>1</c> or <c>true</c> to register the <c>execute</c> tool.</summary>
    public const string EnvironmentVariable = "MCP_ORCHESTRATOR_CODE_MODE";

    /// <summary>Whether code mode is switched on in the given environment (process environment by default).</summary>
    public static bool IsEnabled(Func<string, string?>? getEnvironmentVariable = null)
    {
        var value = (getEnvironmentVariable ?? Environment.GetEnvironmentVariable)(EnvironmentVariable);
        return value is not null
            && (value.Trim() == "1" || value.Trim().Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Tool <c>execute</c>: runs the snippet against the configured capabilities.</summary>
    [McpServerTool(Name = "execute")]
    [Description(
        "Run a short JavaScript snippet that calls downstream tools with " +
        "`await route(capability, tool, args)` and returns only what you need. Use it when a task " +
        "needs several calls in a row, or a large result must be filtered, counted or joined; for a " +
        "single call use 'route' directly. The snippet is the body of an async function — end with " +
        "`return <value>`. route returns the tool's result as parsed JSON (or a string) and throws " +
        "on failure (error.details holds the full response). Tool names and arguments are the same " +
        "as for 'route'. No I/O, modules or network; limits: 50 route calls, 30 s.")]
    public static Task<string> Execute(
        IDownstreamConnectionManager connections,
        ICapabilityCatalog catalog,
        ILogger<OrchestratorTool> routeLogger,
        ILogger<CodeModeTool> logger,
        [Description("JavaScript body of an async function, e.g. \"const rows = await route('db','query',{sql:'…'}); return rows.filter(r => r.status === 'ERR');\"")]
        string code,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("execute ({Length} chars)", code?.Length ?? 0);

        return CodeModeRunner.RunAsync(
            code ?? string.Empty,
            (capability, tool, arguments, ct) =>
                OrchestratorTool.Route(connections, catalog, routeLogger, capability, tool, arguments, ct),
            CodeModeLimits.Default,
            cancellationToken);
    }
}
