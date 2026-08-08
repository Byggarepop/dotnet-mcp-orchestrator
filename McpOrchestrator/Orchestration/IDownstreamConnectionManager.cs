using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Manages live connections to downstream MCP servers. The orchestrator acts as an MCP
/// <em>client</em> here: it launches each capability's server on first use, caches the
/// connection, and proxies tool discovery and tool calls to it.
/// </summary>
public interface IDownstreamConnectionManager
{
    /// <summary>
    /// Lists the tools a downstream capability exposes (connecting to it if needed).
    /// </summary>
    /// <exception cref="CapabilityNotFoundException">No enabled capability has that name.</exception>
    Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string capability, CancellationToken cancellationToken);

    /// <summary>
    /// Invokes a tool on a downstream capability and returns its raw result (connecting
    /// to the capability if needed).
    /// </summary>
    /// <exception cref="CapabilityNotFoundException">No enabled capability has that name.</exception>
    Task<CallToolResult> CallToolAsync(
        string capability,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken);

    /// <summary>
    /// Marks the current position in a capability's captured stderr stream, so lines written
    /// after this point can be retrieved with <see cref="StderrSince"/>. Take a mark before
    /// dispatching a call to scope the capture to that call. Returns 0 when the capability has
    /// produced no stderr yet (or the implementation does not capture stderr).
    /// </summary>
    long StderrMark(string capability) => 0;

    /// <summary>
    /// Returns the stderr lines a capability's server process has written since the given mark
    /// (bounded — old lines may have been evicted). Empty when nothing was captured. Used to
    /// relay downstream failure detail that only reaches the process's stderr — e.g. the MCP
    /// SDK genericizes unhandled tool exceptions to "An error occurred invoking '&lt;tool&gt;'"
    /// and logs the real cause to stderr, which the calling model cannot otherwise see.
    /// </summary>
    IReadOnlyList<string> StderrSince(string capability, long mark) => [];
}

/// <summary>
/// Runtime lifecycle control over downstream connections, used by the config reload pipeline.
/// Split from <see cref="IDownstreamConnectionManager"/> because routing never needs it — and so
/// reload tests can spy the lifecycle without spawning real processes.
/// </summary>
public interface IDownstreamConnectionLifecycle
{
    /// <summary>
    /// Retires the cached connection for a capability, if one exists: waits for in-flight calls
    /// against it to drain (they complete or hit their normal timeouts), then disposes it. Calls
    /// arriving after the retirement connect fresh using the then-current catalog definition.
    /// No-op when the capability has no cached connection.
    /// </summary>
    Task InvalidateAsync(string capability, CancellationToken cancellationToken);
}

/// <summary>Thrown when a request names a capability that is not in the catalog.</summary>
public sealed class CapabilityNotFoundException(string capability, IReadOnlyList<string> available)
    : Exception($"Unknown capability '{capability}'. Available: {(available.Count == 0 ? "(none)" : string.Join(", ", available))}.")
{
    /// <summary>The capability name that was requested.</summary>
    public string Capability { get; } = capability;

    /// <summary>The names of capabilities that <em>are</em> available.</summary>
    public IReadOnlyList<string> Available { get; } = available;
}
