using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Receives a (re)loaded <c>skills</c> config section and applies it: builds the configured
/// sources, loads their skills, and swaps the governance-filtered result into
/// <see cref="SkillRegistry"/>.
/// </summary>
internal interface ISkillsReloadSink
{
    /// <summary>Applies a new skills section (<c>null</c> = skills off). Never throws.</summary>
    Task ApplyAsync(SkillsOptions? options, CancellationToken cancellationToken);
}

/// <summary>Why a skill catalog rebuild runs; carried into the log so the stages can be told apart.</summary>
internal enum SkillRebuildTrigger
{
    /// <summary>The startup config's skills section.</summary>
    Startup,

    /// <summary>A reloaded config (file watcher or central poll) handed over its skills section.</summary>
    ConfigChange,

    /// <summary>A file event under a <c>directory</c> source.</summary>
    DirectoryChange,

    /// <summary>A <c>git</c>/<c>http</c> source's poll timer fired.</summary>
    ScheduledRefresh,
}

/// <summary>
/// Owns the live skills state: builds <see cref="ISkillSource"/> instances from config, keeps
/// them fresh (a debounced <see cref="FileSystemWatcher"/> per directory source, a poll timer per
/// git/http source), and atomically swaps rebuilt catalogs into <see cref="SkillRegistry"/>.
/// Startup reads the skills section off the already-loaded capability catalog; config hot reloads
/// arrive via <see cref="ISkillsReloadSink"/> from <see cref="Reload.ConfigReloader"/> — both the
/// file-watch and central-polling modes flow through the same path.
/// </summary>
/// <remarks>
/// Any trigger rebuilds ALL sources rather than tracking per-source dirtiness: a rebuild is a
/// directory scan plus (for remote sources) a conditional fetch, cheap at realistic skill counts,
/// and one code path is far easier to keep correct across the swap. Rebuilds are serialized and
/// coalesced — a trigger arriving mid-rebuild queues exactly one follow-up rebuild.
/// </remarks>
public sealed class SkillsReloadService : IHostedService, ISkillsReloadSink, IDisposable
{
    /// <summary>Debounce for directory-source file events, matching <see cref="Reload.ConfigFileWatchTrigger"/>.</summary>
    internal static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(500);

    private const int MinPollSeconds = 10;
    private const int DefaultPollSeconds = 300;

    private readonly SkillRegistry _registry;
    private readonly CapabilityRegistry _config;
    private readonly ILogger<SkillsReloadService> _logger;
    private readonly string _cacheRoot;
    private readonly HttpMessageHandler? _httpHandler;

    private readonly SemaphoreSlim _rebuildGate = new(1, 1);
    private int _rebuildQueued;
    private SkillRebuildTrigger _pendingTrigger;

    private readonly object _stateLock = new();
    private List<ISkillSource> _sources = new();
    private SkillsOptions? _appliedOptions;
    private readonly List<IDisposable> _watchersAndTimers = new();
    private Timer? _debounceTimer;
    private bool _disposed;

    public SkillsReloadService(
        SkillRegistry registry,
        CapabilityRegistry config,
        ILogger<SkillsReloadService> logger,
        string? cacheRoot = null,
        HttpMessageHandler? httpHandler = null)
    {
        _registry = registry;
        _config = config;
        _logger = logger;
        _cacheRoot = cacheRoot ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcpOrchestrator", "skills-cache");
        _httpHandler = httpHandler;
    }

    /// <summary>
    /// Applies the startup config's skills section. In central-config mode the startup catalog is
    /// an empty seed and this is a no-op; the section arrives via the sink once the central fetch
    /// applies. Must be registered after the config services and before the MCP server.
    /// </summary>
    public Task StartAsync(CancellationToken cancellationToken)
        => ApplyAsync(_config.Current.Skills, SkillRebuildTrigger.Startup, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task ApplyAsync(SkillsOptions? options, CancellationToken cancellationToken)
        => ApplyAsync(options, SkillRebuildTrigger.ConfigChange, cancellationToken);

    /// <summary>Applies a skills section, attributing the resulting rebuild to <paramref name="trigger"/>.</summary>
    internal async Task ApplyAsync(SkillsOptions? options, SkillRebuildTrigger trigger, CancellationToken cancellationToken)
    {
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

                LogSourceChanges(_appliedOptions, options, trigger);
                _appliedOptions = options;

                TearDownSources();
                if (options is null || !options.Enabled)
                {
                    _sources = new List<ISkillSource>();
                    Governance = new SkillGovernance(new SkillGovernanceOptions());
                    Delivery = new SkillDeliveryOptions();
                }
                else
                {
                    _sources = BuildSources(options);
                    Governance = new SkillGovernance(options.Governance);
                    Delivery = options.Delivery;
                    SetUpTriggers(options);
                }
            }

            await RebuildAsync(trigger, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "applying skills config failed; keeping the current skill catalog");
        }
    }

    /// <summary>
    /// Logs what a new skills section changes relative to the applied one, before any slow source
    /// work starts: sources added/removed and, per surviving id, which settings differ. Credential
    /// fields are reported as changed, never by value.
    /// </summary>
    private void LogSourceChanges(SkillsOptions? previous, SkillsOptions? next, SkillRebuildTrigger trigger)
    {
        var before = previous is { Enabled: true } ? previous.Sources : [];
        var after = next is { Enabled: true } ? next.Sources : [];
        _logger.LogInformation(
            "skills config apply starting; trigger={Trigger}; {Count} source(s) configured ({Ids})",
            Describe(trigger), after.Count, string.Join(", ", after.Select(s => $"{s.Id}:{s.Type}")));

        if (trigger == SkillRebuildTrigger.Startup)
        {
            return; // Nothing to diff against yet.
        }

        if (previous is { Enabled: true } != next is { Enabled: true })
        {
            _logger.LogInformation("skills {State} by this config", next is { Enabled: true } ? "enabled" : "disabled");
        }

        var beforeById = before.ToDictionary(s => s.Id, StringComparer.Ordinal);
        var afterById = after.ToDictionary(s => s.Id, StringComparer.Ordinal);

        foreach (var id in beforeById.Keys.Where(id => !afterById.ContainsKey(id)))
        {
            _logger.LogInformation("skill source removed: {Id}", id);
        }

        foreach (var (id, source) in afterById)
        {
            if (!beforeById.TryGetValue(id, out var old))
            {
                _logger.LogInformation("skill source added: {Id} ({Type})", id, source.Type);
                continue;
            }

            var changed = DescribeChanges(old, source);
            if (changed.Count > 0)
            {
                _logger.LogInformation("skill source changed: {Id}; {Changes}", id, string.Join("; ", changed));
            }
        }
    }

    private static List<string> DescribeChanges(SkillSourceOptions old, SkillSourceOptions next)
    {
        var changes = new List<string>();
        if (!string.Equals(old.Type, next.Type, StringComparison.OrdinalIgnoreCase)) changes.Add($"type {old.Type} -> {next.Type}");
        if (old.Path != next.Path) changes.Add($"path {old.Path ?? "(none)"} -> {next.Path ?? "(none)"}");
        if (old.Url != next.Url) changes.Add($"url {Redact(old.Url)} -> {Redact(next.Url)}");
        if (old.Ref != next.Ref) changes.Add($"ref {old.Ref ?? "(default)"} -> {next.Ref ?? "(default)"}");
        if (old.IndexUrl != next.IndexUrl) changes.Add($"indexUrl {old.IndexUrl ?? "(none)"} -> {next.IndexUrl ?? "(none)"}");
        if (old.PollSeconds != next.PollSeconds) changes.Add($"pollSeconds {old.PollSeconds?.ToString() ?? "default"} -> {next.PollSeconds?.ToString() ?? "default"}");
        if (old.Token != next.Token) changes.Add("token changed");
        if (old.Authorization != next.Authorization) changes.Add("authorization changed");
        return changes;

        static string Redact(string? url) => url is null ? "(none)" : GitSkillSource.RedactUrl(url);
    }

    /// <summary>The governance rules of the applied config (empty rules before the first apply).</summary>
    internal SkillGovernance Governance { get; private set; } = new(new SkillGovernanceOptions());

    /// <summary>The delivery flags of the applied config, read by tools/resource handlers per call.</summary>
    internal SkillDeliveryOptions Delivery { get; private set; } = new();

    /// <summary>
    /// Loads every source and swaps the merged catalog in. Serialized; a trigger firing
    /// mid-rebuild coalesces into exactly one follow-up pass so no change is missed.
    /// </summary>
    internal async Task RebuildAsync(SkillRebuildTrigger trigger, CancellationToken cancellationToken)
    {
        lock (_stateLock)
        {
            _pendingTrigger = trigger;
        }

        Interlocked.Exchange(ref _rebuildQueued, 1);
        while (Interlocked.CompareExchange(ref _rebuildQueued, 0, 1) == 1)
        {
            await _rebuildGate.WaitAsync(cancellationToken);
            try
            {
                List<ISkillSource> sources;
                SkillGovernance governance;
                SkillRebuildTrigger reason;
                lock (_stateLock)
                {
                    sources = _sources;
                    governance = Governance;
                    reason = _pendingTrigger;
                }

                var discovered = new List<SkillSnapshot>();
                var contributions = new List<string>();
                foreach (var source in sources)
                {
                    var load = await source.LoadAsync(cancellationToken);
                    discovered.AddRange(load.Skills);
                    contributions.Add($"{source.Id}={load.Skills.Count} {Describe(load.Freshness)}");
                    _logger.LogInformation(
                        "skill source {Source}: {Count} skill(s) loaded from {Freshness}",
                        source.Id, load.Skills.Count, Describe(load.Freshness));
                }

                var catalog = SkillCatalog.Build(discovered, governance, _logger);
                _registry.Swap(catalog);
                _logger.LogInformation(
                    "skill catalog rebuilt: {Count} skill(s) served; trigger={Trigger}; sources: {Sources} ({Names})",
                    catalog.Skills.Count, Describe(reason),
                    contributions.Count == 0 ? "none" : string.Join(", ", contributions),
                    string.Join(", ", catalog.Skills.Select(s => s.Name)));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "skill catalog rebuild failed; keeping the current catalog");
            }
            finally
            {
                _rebuildGate.Release();
            }
        }
    }

    private static string Describe(SkillRebuildTrigger trigger) => trigger switch
    {
        SkillRebuildTrigger.Startup => "startup",
        SkillRebuildTrigger.ConfigChange => "config-change",
        SkillRebuildTrigger.DirectoryChange => "directory-change",
        SkillRebuildTrigger.ScheduledRefresh => "scheduled-refresh",
        _ => trigger.ToString(),
    };

    private static string Describe(SkillSourceFreshness freshness) => freshness switch
    {
        SkillSourceFreshness.Fresh => "fresh content",
        SkillSourceFreshness.Cached => "cached content (origin unreachable)",
        SkillSourceFreshness.Unavailable => "nothing (origin unreachable, no cache)",
        _ => freshness.ToString(),
    };

    private List<ISkillSource> BuildSources(SkillsOptions options)
    {
        var sources = new List<ISkillSource>();
        foreach (var source in options.Sources)
        {
            switch (source.Type.ToLowerInvariant())
            {
                case "directory":
                    sources.Add(new DirectorySkillSource(source.Id, source.Path!, _logger));
                    break;
                case "git":
                    sources.Add(new GitSkillSource(
                        source.Id, source.Url!, source.Ref, source.Token, _cacheRoot, _logger, EffectivePollSeconds(source)));
                    break;
                case "http":
                    sources.Add(new HttpSkillSource(source.Id, new Uri(source.IndexUrl!), source.Authorization, _logger, _httpHandler));
                    break;
            }
        }

        return sources;
    }

    private static int EffectivePollSeconds(SkillSourceOptions source)
        => Math.Max(MinPollSeconds, source.PollSeconds ?? DefaultPollSeconds);

    /// <summary>Wires a debounced watcher per directory source and a jittered poll timer per remote source.</summary>
    private void SetUpTriggers(SkillsOptions options)
    {
        foreach (var source in options.Sources)
        {
            if (string.Equals(source.Type, "directory", StringComparison.OrdinalIgnoreCase))
            {
                if (!Directory.Exists(source.Path!))
                {
                    continue; // Logged by the scanner; a watcher on a missing root would throw.
                }

                var watcher = new FileSystemWatcher(source.Path!)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite,
                };
                watcher.Changed += (_, _) => ScheduleDebouncedRebuild();
                watcher.Created += (_, _) => ScheduleDebouncedRebuild();
                watcher.Deleted += (_, _) => ScheduleDebouncedRebuild();
                watcher.Renamed += (_, _) => ScheduleDebouncedRebuild();
                watcher.EnableRaisingEvents = true;
                _watchersAndTimers.Add(watcher);
            }
            else
            {
                var period = TimeSpan.FromSeconds(EffectivePollSeconds(source));
                _watchersAndTimers.Add(new Timer(
                    _ => _ = RebuildSafelyAsync(SkillRebuildTrigger.ScheduledRefresh), null, period, period));
            }
        }
    }

    private void ScheduleDebouncedRebuild()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _debounceTimer?.Dispose();
            _debounceTimer = new Timer(
                _ => _ = RebuildSafelyAsync(SkillRebuildTrigger.DirectoryChange), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Timer callbacks have no ambient error handling; RebuildAsync already never throws
    /// except for cancellation, which is irrelevant on a timer path.</summary>
    private async Task RebuildSafelyAsync(SkillRebuildTrigger trigger)
    {
        try
        {
            await RebuildAsync(trigger, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "background skill rebuild failed");
        }
    }

    private void TearDownSources()
    {
        foreach (var disposable in _watchersAndTimers)
        {
            disposable.Dispose();
        }

        _watchersAndTimers.Clear();
        foreach (var source in _sources.OfType<IDisposable>())
        {
            source.Dispose();
        }
    }

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            TearDownSources();
            _debounceTimer?.Dispose();
        }
    }
}
