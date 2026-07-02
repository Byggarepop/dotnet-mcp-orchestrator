namespace McpOrchestrator.Orchestration;

/// <summary>
/// The live, hot-swappable capability catalog. All readers (the meta-tools, the connection
/// manager) resolve through this; a reload replaces the underlying immutable
/// <see cref="CapabilityCatalog"/> snapshot in one atomic reference write, so readers are
/// lock-free and always see a complete, consistent catalog — never a half-applied one.
/// </summary>
public sealed class CapabilityRegistry : ICapabilityCatalog
{
    private volatile CapabilityCatalog _current;

    public CapabilityRegistry(CapabilityCatalog initial) => _current = initial;

    /// <summary>The current immutable snapshot (grab once when multiple reads must be consistent).</summary>
    public CapabilityCatalog Current => _current;

    /// <inheritdoc />
    public IReadOnlyList<CapabilityDescriptor> Capabilities => _current.Capabilities;

    /// <inheritdoc />
    public IReadOnlyList<string> Names => _current.Names;

    /// <inheritdoc />
    public CapabilityDescriptor? Find(string name) => _current.Find(name);

    /// <summary>Makes <paramref name="next"/> the live catalog. Called only by the reload pipeline.</summary>
    internal void Swap(CapabilityCatalog next) => _current = next;
}
