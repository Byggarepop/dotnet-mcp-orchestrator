using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpOrchestrator.DemoMcp;

/// <summary>
/// Stand-in downstream used to exercise the orchestrator's failure handling: a tool that
/// echoes its input (argument round-trip), one that always throws (so the result comes back
/// with <c>IsError = true</c>), and one that sleeps (so a connect/call timeout can fire).
/// Selected by the <c>diag</c> persona; not part of the normal jira/codegen demo.
/// </summary>
[McpServerToolType]
public sealed class DiagnosticsTools
{
    /// <summary>Returns the supplied message verbatim — used to assert arguments round-trip intact.</summary>
    [McpServerTool(Name = "echo")]
    [Description("Echo the supplied message back unchanged.")]
    public static string Echo(
        [Description("The message to echo back.")] string message)
        => message;

    /// <summary>Always throws, so the MCP server returns a tool result flagged <c>IsError = true</c>.</summary>
    [McpServerTool(Name = "fail")]
    [Description("Always fails — used to test downstream error handling.")]
    public static string Fail(
        [Description("Optional reason included in the failure message.")] string? reason = null)
        => throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(reason) ? "Intentional failure (diagnostics)." : reason);

    /// <summary>Sleeps for the requested number of milliseconds, then returns — used to trigger timeouts.</summary>
    [McpServerTool(Name = "slow")]
    [Description("Sleep for the given number of milliseconds, then return 'done'.")]
    public static async Task<string> Slow(
        [Description("How long to sleep, in milliseconds.")] int delayMs)
    {
        await Task.Delay(Math.Max(0, delayMs));
        return "done";
    }
}
