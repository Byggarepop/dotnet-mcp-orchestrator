using System.Collections.Concurrent;

namespace ConsafeWorkflow.Mcp.Workflow;

/// <summary>
/// Process-lifetime, in-memory <see cref="ISessionStore"/> backed by a dictionary.
/// State is lost when the server stops; replace with a persistent store to retain it.
/// </summary>
public sealed class InMemorySessionStore : ISessionStore
{
    private readonly ConcurrentDictionary<string, WorkflowSession> _sessions = new();

    public WorkflowSession? Get(string sessionId) =>
        _sessions.TryGetValue(sessionId, out var session) ? session : null;

    public WorkflowSession GetOrCreate(string sessionId) =>
        _sessions.GetOrAdd(sessionId, id => new WorkflowSession { SessionId = id });

    public void Save(WorkflowSession session)
    {
        session.UpdatedAt = DateTimeOffset.UtcNow;
        _sessions[session.SessionId] = session;
    }
}
