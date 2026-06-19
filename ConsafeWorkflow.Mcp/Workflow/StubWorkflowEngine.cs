namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Placeholder engine for the shell. Returns a fixed message for all inputs and does
/// not advance any real workflow. Replace with the concrete state machine.
/// </summary>
public sealed class StubWorkflowEngine : IWorkflowEngine
{
    public const string NotImplementedMessage = "MCP shell — not yet implemented";
    public const string OpeningMessage =
        "MCP shell ready — engine not yet implemented. Type anything to continue.";

    private readonly ILocalModelClient _localModel;

    public StubWorkflowEngine(ILocalModelClient localModel) => _localModel = localModel;

    public WorkflowStep InitialStep => new(OpeningMessage);

    public async Task<WorkflowStep> AdvanceAsync(
        WorkflowSession session,
        string userInput,
        CancellationToken cancellationToken = default)
    {
        // The seam is real: we transition off New so callers can observe state moving,
        // even though no actual workflow steps exist yet.
        if (session.State == WorkflowState.New)
        {
            session.State = WorkflowState.InProgress;
        }

        // Demonstrate the local-model round-trip: hand the user's answer to the local model
        // (mocked for now) and fold its reply into the directed message. A real engine would
        // use the reply to drive the next step — and could instead return DelegateToAgent (let
        // Copilot do the work) or Complete (everything handled locally, stop).
        var reply = await _localModel.CompleteAsync(
            systemPrompt: "You are a local workflow assistant.",
            userPrompt: userInput,
            cancellationToken);

        return new WorkflowStep(
            $"{NotImplementedMessage} — local model said: {reply}",
            WorkflowOutcome.AwaitUser);
    }
}
