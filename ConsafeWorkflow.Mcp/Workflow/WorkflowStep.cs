namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// What should happen with a <see cref="WorkflowStep.Message"/> after the engine advances.
/// Lets one workflow mix two styles: reason locally then hand off to Copilot, or do all the
/// work locally and stop.
/// </summary>
public enum WorkflowOutcome
{
    /// <summary>
    /// Present the message to the user and wait for their next answer — the normal
    /// question/answer loop. The server collects the next answer (via elicitation).
    /// </summary>
    AwaitUser,

    /// <summary>
    /// Present the message as an instruction for the Copilot agent to act on — e.g. the
    /// local model reasoned about the request and Copilot does the actual work.
    /// </summary>
    DelegateToAgent,

    /// <summary>
    /// The workflow handled everything (e.g. fully locally) and is finished — present the
    /// message as the final result and stop.
    /// </summary>
    Complete,
}

/// <summary>
/// The result of advancing the workflow one step: the directed message to surface, plus
/// what should happen next.
/// </summary>
/// <param name="Message">The directed message to present verbatim to the user/agent.</param>
/// <param name="Outcome">What the agent should do next with the message.</param>
/// <param name="Choices">
/// Optional fixed set of options for an <see cref="WorkflowOutcome.AwaitUser"/> step. When
/// present, the server asks the user to pick one (rendered as a single-select menu/dropdown
/// via elicitation) rather than typing free text.
/// </param>
public sealed record WorkflowStep(
    string Message,
    WorkflowOutcome Outcome = WorkflowOutcome.AwaitUser,
    IReadOnlyList<string>? Choices = null);
