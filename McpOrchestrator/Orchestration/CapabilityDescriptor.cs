namespace McpOrchestrator.Orchestration;

/// <summary>
/// One downstream MCP server the orchestrator can route to — a single "capability"
/// such as <c>jira</c>, <c>codegen</c>, or <c>db</c>. This is the "connection + its
/// instructions to another MCP": <see cref="Command"/>/<see cref="Args"/> say how to
/// launch (connect to) it, while <see cref="Summary"/>/<see cref="Instructions"/> tell
/// the orchestrating model when and how to use it.
/// </summary>
/// <remarks>
/// Populated from the orchestrator config file (see <see cref="CapabilityCatalog"/>).
/// Only the stdio transport is supported in this prototype.
/// </remarks>
public sealed class CapabilityDescriptor
{
    /// <summary>Short, stable id the model uses to address this capability, e.g. "jira".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>One-line description of what this capability is for. Surfaced to the model.</summary>
    public string Summary { get; set; } = string.Empty;

    /// <summary>
    /// Usage guidance shown to the model: when to pick this capability and what to
    /// provide (e.g. "include the issue key like PROJ-123 when you have it").
    /// </summary>
    public string Instructions { get; set; } = string.Empty;

    /// <summary>When false the capability is ignored (not advertised, not connectable).</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Transport kind. Only <c>stdio</c> is implemented in the prototype.</summary>
    public string Transport { get; set; } = "stdio";

    /// <summary>Executable to launch the downstream MCP server, e.g. "dotnet" or "npx".</summary>
    public string Command { get; set; } = string.Empty;

    /// <summary>Arguments passed to <see cref="Command"/>. Supports <c>${VAR}</c> substitution.</summary>
    public List<string> Args { get; set; } = new();

    /// <summary>Working directory for the spawned server. Supports <c>${VAR}</c> substitution.</summary>
    public string? WorkingDirectory { get; set; }

    /// <summary>Extra environment variables for the spawned server. Values support <c>${VAR}</c>.</summary>
    public Dictionary<string, string?> Env { get; set; } = new();

    /// <summary>
    /// Seconds to wait for this downstream server to start and complete the MCP handshake
    /// before giving up. <c>null</c> uses the orchestrator default. Raise it for slow
    /// first-launches (e.g. an <c>npx</c> server that downloads its package on first run).
    /// </summary>
    public int? ConnectTimeoutSeconds { get; set; }

    /// <summary>
    /// Seconds to wait for a single tool call or tool-list to return before giving up.
    /// <c>null</c> uses the orchestrator default. A timeout faults the call but leaves the
    /// connection cached for the next request.
    /// </summary>
    public int? CallTimeoutSeconds { get; set; }
}

/// <summary>Root of the orchestrator configuration file.</summary>
public sealed class OrchestratorConfig
{
    /// <summary>The downstream MCP servers this orchestrator can reach.</summary>
    public List<CapabilityDescriptor> Capabilities { get; set; } = new();
}
