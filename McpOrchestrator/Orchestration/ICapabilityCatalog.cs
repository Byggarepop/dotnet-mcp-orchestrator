namespace McpOrchestrator.Orchestration;

/// <summary>
/// The set of downstream MCP capabilities the orchestrator knows about, loaded from
/// configuration. This is the orchestrator's address book — it answers "what can I
/// route to?" without opening any connections.
/// </summary>
public interface ICapabilityCatalog
{
    /// <summary>All enabled capabilities, in configuration order.</summary>
    IReadOnlyList<CapabilityDescriptor> Capabilities { get; }

    /// <summary>The names of all enabled capabilities (for error messages and discovery).</summary>
    IReadOnlyList<string> Names { get; }

    /// <summary>
    /// Finds an enabled capability by name (case-insensitive), or <c>null</c> if there
    /// is no such enabled capability.
    /// </summary>
    CapabilityDescriptor? Find(string name);
}
