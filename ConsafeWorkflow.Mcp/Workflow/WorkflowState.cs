namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// High-level lifecycle state of a workflow session.
/// Extend this as concrete component-creation workflows are added.
/// </summary>
public enum WorkflowState
{
    /// <summary>No prior interaction; the next call should return the initialization message.</summary>
    New = 0,

    /// <summary>The state machine is mid-workflow and advancing through steps.</summary>
    InProgress = 1,

    /// <summary>The workflow finished; no further directed instructions remain.</summary>
    Completed = 2,
}
