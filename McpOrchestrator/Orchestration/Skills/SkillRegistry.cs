namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Hot-swappable holder for the current <see cref="SkillCatalog"/>, mirroring
/// <see cref="CapabilityRegistry"/>: tools and resource handlers resolve skills through this
/// registry on every call, so a reload swap is visible to the next request with no locking.
/// </summary>
public sealed class SkillRegistry
{
    private volatile SkillCatalog _current = SkillCatalog.Empty;

    /// <summary>The catalog serving requests right now.</summary>
    internal SkillCatalog Current => _current;

    /// <summary>Atomically replaces the served catalog (reload path only).</summary>
    internal void Swap(SkillCatalog next) => _current = next;
}
