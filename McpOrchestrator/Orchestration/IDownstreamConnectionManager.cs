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
