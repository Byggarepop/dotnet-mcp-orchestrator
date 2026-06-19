using System.Text.Json.Serialization;

namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Serializable state for a single workflow conversation, keyed by session id.
/// Kept as a plain POCO (System.Text.Json friendly) so it can later be persisted
/// to a durable store without changing the engine contract.
/// </summary>
public sealed class WorkflowSession
{
    /// <summary>Stable identifier supplied by the agent for the conversation.</summary>
    public string SessionId { get; set; } = string.Empty;

    /// <summary>Current lifecycle state of the session.</summary>
    public WorkflowState State { get; set; } = WorkflowState.New;

    /// <summary>
    /// Zero-based index of the current step within the active workflow.
    /// Reserved for the state machine; unused by the shell.
    /// </summary>
    public int StepIndex { get; set; }

    /// <summary>
    /// Free-form bag of values collected during the workflow (e.g. chosen pattern,
    /// component name, parameter suggestions). Reserved for future steps.
    /// </summary>
    public Dictionary<string, string> Data { get; set; } = new();

    /// <summary>
    /// The most recent directed message produced by the engine — i.e. the question the
    /// user is currently expected to answer. Used to label the elicitation prompt on the
    /// next turn so the user is asked directly (bypassing agent-relayed input).
    /// </summary>
    public string? PendingPrompt { get; set; }

    /// <summary>
    /// The fixed options for <see cref="PendingPrompt"/>, if it is a menu/choice step.
    /// When set, the next elicitation is rendered as a single-select picker.
    /// </summary>
    public List<string>? PendingChoices { get; set; }

    /// <summary>UTC timestamp of creation.</summary>
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>UTC timestamp of the most recent update.</summary>
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    [JsonIgnore]
    public bool IsNew => State == WorkflowState.New;
}
