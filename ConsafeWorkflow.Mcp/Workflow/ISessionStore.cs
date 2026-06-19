namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Storage abstraction for <see cref="WorkflowSession"/> instances.
/// The shell uses an in-memory implementation; this seam allows swapping in a
/// durable store (file, database, etc.) later without touching the engine.
/// </summary>
public interface ISessionStore
{
    /// <summary>Returns the session for <paramref name="sessionId"/>, or null if none exists.</summary>
    WorkflowSession? Get(string sessionId);

    /// <summary>Returns the existing session or creates, stores and returns a new one.</summary>
    WorkflowSession GetOrCreate(string sessionId);

    /// <summary>Persists changes to a session.</summary>
    void Save(WorkflowSession session);
}
