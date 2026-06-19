using ModelContextProtocol.Client;

namespace ConsafeWorkflow.Mcp.Orchestration;

/// <summary>
/// Turns a natural-language need ("get the status of PROJ-123") into a concrete
/// downstream tool call: which tool to invoke and with what arguments. This is the
/// seam where a real model belongs — a local LLM (Ollama/LM Studio) or a cloud model
/// would do this far better than the shipped heuristic. The <c>request</c> tool uses
/// this so the agent can delegate tool selection to the orchestrator instead of
/// inspecting the tool list itself.
/// </summary>
public interface IRoutePlanner
{
    /// <summary>
    /// Chooses a tool from <paramref name="tools"/> and builds its arguments to satisfy
    /// <paramref name="request"/>.
    /// </summary>
    /// <returns>The plan, or <c>null</c> if the capability exposes no usable tool.</returns>
    Task<RoutePlan?> PlanAsync(
        string capability,
        IReadOnlyList<McpClientTool> tools,
        string request,
        CancellationToken cancellationToken);
}

/// <summary>A planner's decision: the tool to call, the arguments, and why.</summary>
public sealed record RoutePlan(
    string Tool,
    IReadOnlyDictionary<string, object?> Arguments,
    string Rationale);
