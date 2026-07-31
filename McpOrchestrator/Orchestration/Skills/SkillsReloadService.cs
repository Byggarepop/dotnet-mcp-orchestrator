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

    private readonly object _stateLock = new();
    private List<ISkillSource> _sources = new();
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
        => ApplyAsync(_config.Current.Skills, cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task ApplyAsync(SkillsOptions? options, CancellationToken cancellationToken)
    {
        try
        {
            lock (_stateLock)
            {
                if (_disposed)
                {
                    return;
                }

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

            await RebuildAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "applying skills config failed; keeping the current skill catalog");
        }
    }

    /// <summary>The governance rules of the applied config (empty rules before the first apply).</summary>
    internal SkillGovernance Governance { get; private set; } = new(new SkillGovernanceOptions());

    /// <summary>The delivery flags of the applied config, read by tools/resource handlers per call.</summary>
    internal SkillDeliveryOptions Delivery { get; private set; } = new();

    /// <summary>
    /// Loads every source and swaps the merged catalog in. Serialized; a trigger firing
    /// mid-rebuild coalesces into exactly one follow-up pass so no change is missed.
    /// </summary>
    internal async Task RebuildAsync(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _rebuildQueued, 1);
        while (Interlocked.CompareExchange(ref _rebuildQueued, 0, 1) == 1)
        {
            await _rebuildGate.WaitAsync(cancellationToken);
            try
            {
                List<ISkillSource> sources;
                SkillGovernance governance;
                lock (_stateLock)
                {
                    sources = _sources;
                    governance = Governance;
                }

                var discovered = new List<SkillSnapshot>();
                foreach (var source in sources)
                {
                    discovered.AddRange(await source.LoadAsync(cancellationToken));
                }

                var catalog = SkillCatalog.Build(discovered, governance, _logger);
                _registry.Swap(catalog);
                _logger.LogInformation(
                    "skill catalog rebuilt: {Count} skill(s) served ({Names})",
                    catalog.Skills.Count, string.Join(", ", catalog.Skills.Select(s => s.Name)));
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
                    sources.Add(new GitSkillSource(source.Id, source.Url!, source.Ref, source.Token, _cacheRoot, _logger));
                    break;
                case "http":
                    sources.Add(new HttpSkillSource(source.Id, new Uri(source.IndexUrl!), source.Authorization, _logger, _httpHandler));
                    break;
            }
        }

        return sources;
    }

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
                var seconds = Math.Max(MinPollSeconds, source.PollSeconds ?? DefaultPollSeconds);
                var period = TimeSpan.FromSeconds(seconds);
                _watchersAndTimers.Add(new Timer(
                    _ => _ = RebuildSafelyAsync(), null, period, period));
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
            _debounceTimer = new Timer(_ => _ = RebuildSafelyAsync(), null, Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Timer callbacks have no ambient error handling; RebuildAsync already never throws
    /// except for cancellation, which is irrelevant on a timer path.</summary>
    private async Task RebuildSafelyAsync()
    {
        try
        {
            await RebuildAsync(CancellationToken.None);
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
