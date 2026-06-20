using ModelContextProtocol.Client;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Interprets a natural-language <c>request</c> (e.g. "create a class with an Id and a Name") into a
/// concrete downstream tool call — which tool, and with what arguments.
///
/// <para><b>Why this exists, and when it is NOT needed.</b> The preferred path — the <c>route</c>
/// tool — needs <i>no</i> planner: the agent already names the exact tool and supplies the arguments
/// (it gets the schemas from <c>discover_tools</c>), so the orchestrator just forwards them. No
/// interpretation happens. The only thing that needs interpretation is the <c>request</c> tool,
/// where the agent passes a sentence and expects the orchestrator to work out the tool + arguments.
/// That "work it out" step is this planner — so a planner is only ever exercised by <c>request</c>.</para>
///
/// <para><b>Why the implementation sometimes needs an LLM.</b> A planner is only as good as its grasp
/// of language. The default <see cref="HeuristicRoutePlanner"/> does keyword matching plus a couple
/// of regexes — no real understanding; fine for a trivial request, but it mangles anything that
/// needs interpreting (it would dump the whole sentence above into a "className" argument). To make
/// <c>request</c> actually reliable you swap in an LLM-backed planner (the optional embedded local
/// model, or e.g. an Ollama endpoint) that genuinely understands the sentence. You only need an LLM
/// <i>here</i> if you want the <c>request</c> shortcut to handle real natural language; <c>route</c>
/// works perfectly without any of this.</para>
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
