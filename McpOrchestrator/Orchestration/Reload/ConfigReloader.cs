using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// The load + validate → diff + apply core of config hot reload, independent of what triggered it.
/// A rejected load (malformed JSON, invalid entries) leaves the running config untouched
/// (last-known-good). An accepted load is diffed per capability against the previous config:
/// only capabilities whose <em>launch-relevant</em> fields changed (or that were removed) have
/// their downstream connection retired — metadata edits (summary, instructions, enabled) and
/// untouched entries never restart a downstream. The new catalog is swapped in atomically before
/// any connection is retired, so new calls resolve against the new config while in-flight calls
/// drain against the old connections.
/// </summary>
internal sealed class ConfigReloader
{
    private readonly IConfigSource _source;
    private readonly CapabilityRegistry _registry;
    private readonly IDownstreamConnectionLifecycle _connections;
    private readonly ILogger _logger;

    // Reloads are serialized; readers never block (they read the registry's volatile snapshot).
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    // The previous config's full entry list (disabled entries included), which the differ compares
    // against. Seeded from the startup catalog — enabled entries only, so an entry disabled at
    // startup shows up as "added" on its first reload, which costs nothing (no connection exists).
    private IReadOnlyList<CapabilityDescriptor> _lastEntries;

    public ConfigReloader(
        IConfigSource source,
        CapabilityRegistry registry,
        IDownstreamConnectionLifecycle connections,
        ILogger logger)
    {
        _source = source;
        _registry = registry;
        _connections = connections;
        _logger = logger;
        _lastEntries = registry.Current.Capabilities;
    }

    /// <summary>
    /// Runs one full reload cycle: load + validate from the source, then diff + apply. Returns the
    /// applied diff, or <c>null</c> when the new config was rejected and the running one was kept.
    /// Never throws — a reload failure must never take the server down.
    /// </summary>
    public async Task<ReloadDiff?> ReloadAsync(CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await _source.TryLoadAsync(cancellationToken);
            if (loaded is null)
            {
                return null; // Rejection already logged; last-known-good stays live.
            }

            return await ApplyAsync(loaded, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Config reload failed unexpectedly; keeping the current config.");
            return null;
        }
    }

    /// <summary>
    /// Diffs <paramref name="next"/> against the running config and applies it: swap the catalog
    /// snapshot first (list_capabilities and new routes see the new config immediately; routes to
    /// removed capabilities fail as unknown), then retire the connections of removed and
    /// launch-changed capabilities — each retirement drains that capability's in-flight calls
    /// before disposing. Exposed separately so tests (and later trigger implementations that load
    /// from elsewhere) can drive diff + apply directly.
    /// </summary>
    internal async Task<ReloadDiff> ApplyAsync(ReloadedConfig next, CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken);
        try
        {
            var diff = ComputeDiff(_lastEntries, next.AllEntries);

            _registry.Swap(next.Catalog);
            _lastEntries = next.AllEntries;

            foreach (var name in diff.Removed.Concat(diff.Restarted))
            {
                await _connections.InvalidateAsync(name, cancellationToken);
            }

            _logger.LogInformation(
                "Config reloaded from {Source}: {Added} added, {Removed} removed, {Restarted} restarted, "
                + "{MetadataUpdated} updated in place, {Unchanged} unchanged.",
                _source.Description, diff.Added.Count, diff.Removed.Count, diff.Restarted.Count,
                diff.MetadataUpdated.Count, diff.Unchanged);

            return diff;
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    /// <summary>Classifies every capability by name into added / removed / restarted / metadata-only / unchanged.</summary>
    internal static ReloadDiff ComputeDiff(
        IReadOnlyList<CapabilityDescriptor> oldEntries, IReadOnlyList<CapabilityDescriptor> newEntries)
    {
        var oldByName = ByName(oldEntries);
        var newByName = ByName(newEntries);

        var added = new List<string>();
        var removed = new List<string>();
        var restarted = new List<string>();
        var metadataUpdated = new List<string>();
        var unchanged = 0;

        foreach (var name in oldByName.Keys.Where(n => !newByName.ContainsKey(n)))
        {
            removed.Add(name);
        }

        foreach (var (name, entry) in newByName)
        {
            if (!oldByName.TryGetValue(name, out var previous))
            {
                added.Add(name);
            }
            else if (RequiresRestart(previous, entry))
            {
                restarted.Add(name);
            }
            else if (previous.Summary != entry.Summary
                || previous.Instructions != entry.Instructions
                || previous.Enabled != entry.Enabled)
            {
                metadataUpdated.Add(name);
            }
            else
            {
                unchanged++;
            }
        }

        return new ReloadDiff(added, removed, restarted, metadataUpdated, unchanged);
    }

    /// <summary>
    /// True when the two definitions differ in a field that affects the launched process or its
    /// connection, so the old downstream must be retired. Summary/instructions/enabled are
    /// deliberately absent — they change what the agent sees, not what runs.
    /// </summary>
    internal static bool RequiresRestart(CapabilityDescriptor previous, CapabilityDescriptor next) =>
        !string.Equals(previous.Command, next.Command, StringComparison.Ordinal)
        || !previous.Args.SequenceEqual(next.Args, StringComparer.Ordinal)
        || !string.Equals(previous.WorkingDirectory, next.WorkingDirectory, StringComparison.Ordinal)
        || !string.Equals(previous.Transport, next.Transport, StringComparison.OrdinalIgnoreCase)
        || previous.ConnectTimeoutSeconds != next.ConnectTimeoutSeconds
        || previous.CallTimeoutSeconds != next.CallTimeoutSeconds
        || !EnvEquals(previous.Env, next.Env);

    /// <summary>
    /// Value equality for the env block, independent of entry order — a re-serialized config with
    /// identical variables must never read as a change (that would restart the downstream on
    /// every reload).
    /// </summary>
    private static bool EnvEquals(
        IReadOnlyDictionary<string, string?> previous, IReadOnlyDictionary<string, string?> next)
    {
        if (previous.Count != next.Count)
        {
            return false;
        }

        foreach (var (key, value) in previous)
        {
            if (!next.TryGetValue(key, out var other) || !string.Equals(value, other, StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static Dictionary<string, CapabilityDescriptor> ByName(IReadOnlyList<CapabilityDescriptor> entries)
    {
        // First definition wins on duplicates, matching catalog validation.
        var byName = new Dictionary<string, CapabilityDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries)
        {
            byName.TryAdd(entry.Name, entry);
        }

        return byName;
    }
}

/// <summary>What one applied reload did, per capability name. <c>Restarted</c> = launch-relevant
/// fields changed, old connection retired, new one starts lazily on next use.</summary>
internal sealed record ReloadDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Restarted,
    IReadOnlyList<string> MetadataUpdated,
    int Unchanged);
