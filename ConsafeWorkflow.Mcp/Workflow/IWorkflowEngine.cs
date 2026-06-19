namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Drives a workflow forward. Given the current session and the latest user input,
/// it returns the next <em>directed message</em> — the exact text the agent must
/// present to the user verbatim (a question, a clickable choice prompt, or a command
/// to run).
/// <para>
/// This is the primary extension point. The shell ships a <see cref="StubWorkflowEngine"/>;
/// the real state machine that drives component-creation workflows step by step will
/// implement this interface.
/// </para>
/// </summary>
public interface IWorkflowEngine
{
    /// <summary>
    /// The opening step for a brand-new session — the first thing put to the user (e.g. an
    /// entry menu of options, or the first question). Shown via elicitation on the very
    /// first call so the workflow can collect a real answer immediately. May carry
    /// <see cref="WorkflowStep.Choices"/> to render a menu.
    /// </summary>
    WorkflowStep InitialStep { get; }

    /// <summary>
    /// Advances the workflow. For a NEW session this returns the initialization step; for an
    /// established session it advances the state machine and returns the next step. Async so
    /// the engine can call a local model (<see cref="ILocalModelClient"/>) while deciding.
    /// </summary>
    /// <param name="session">The session being advanced (mutated in place; caller saves).</param>
    /// <param name="userInput">The user's latest reply; empty on first invocation.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>
    /// The next <see cref="WorkflowStep"/> — the directed message plus what the agent should
    /// do with it (wait for the user, act on it, or stop).
    /// </returns>
    Task<WorkflowStep> AdvanceAsync(
        WorkflowSession session,
        string userInput,
        CancellationToken cancellationToken = default);
}
