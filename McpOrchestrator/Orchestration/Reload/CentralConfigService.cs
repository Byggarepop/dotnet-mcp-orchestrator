using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Hosts centrally managed config mode (<c>MCP_ORCHESTRATOR_CONFIG_URL</c>): the initial
/// fetch-or-cache load at startup, then the polling trigger feeding the same reload pipeline
/// file mode uses. Registered <em>instead of</em> the file-watch service — source selection is
/// binary, never merged. Startup fails loudly when the URL is unreachable and no usable cached
/// copy exists; it never silently falls back to a local config file.
/// </summary>
internal sealed class CentralConfigService : IHostedService, IDisposable
{
    private readonly CentralConfigOptions _options;
    private readonly CapabilityRegistry _registry;
    private readonly IDownstreamConnectionLifecycle _connections;
    private readonly ILogger<CentralConfigService> _logger;
    private readonly CentralConfigCache _cache;
    private readonly HttpMessageHandler? _handler;
    private HttpConfigPollTrigger? _trigger;

    /// <param name="handler">HTTP handler override for tests; null uses a real one.</param>
    /// <param name="cache">Cache override for tests; null uses <c>~/.mcpOrchestrator</c>.</param>
    public CentralConfigService(
        CentralConfigOptions options,
        CapabilityRegistry registry,
        IDownstreamConnectionLifecycle connections,
        ILogger<CentralConfigService> logger,
        HttpMessageHandler? handler = null,
        CentralConfigCache? cache = null)
    {
        _options = options;
        _registry = registry;
        _connections = connections;
        _logger = logger;
        _handler = handler;
        _cache = cache ?? new CentralConfigCache();
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Central config mode: serving the catalog from {Url} ({Variable} is set).",
            _options.Url, CentralConfigOptions.UrlVariable);
        if (_options.IgnoredLocalConfigPath is not null)
        {
            _logger.LogWarning(
                "MCP_ORCHESTRATOR_CONFIG is also set but IGNORED ({LocalPath}) — the central URL wins, "
                + "and central and local configs are never merged.",
                _options.IgnoredLocalConfigPath);
        }

        var trigger = new HttpConfigPollTrigger(_options, _cache, _logger, _handler);

        // Prime the conditional-request validators from an existing cache for this URL, so an
        // unchanged config costs a 304 even on the first fetch after a restart.
        var url = _options.Url.ToString();
        var cached = _cache.TryRead(url);
        if (cached is not null)
        {
            trigger.Prime(cached.Value.Meta.ETag, cached.Value.Meta.LastModified);
        }

        // Initial load: fetch into the cache; fall back to a URL-matching cached copy when the
        // server is unreachable. No cache either → refuse to start (never the local file).
        var outcome = await trigger.PollOnceAsync(onChanged: null, cancellationToken);
        if (outcome == HttpConfigPollTrigger.PollOutcome.Failed)
        {
            cached = _cache.TryRead(url);
            if (cached is null)
            {
                trigger.Dispose();
                throw new InvalidOperationException(
                    $"Cannot start: the central config at {url} is unreachable or invalid, and no usable "
                    + $"cached copy for that URL exists under ~/.mcpOrchestrator. Fix the URL/network/credential, "
                    + $"or unset {CentralConfigOptions.UrlVariable} to use a local config file.");
            }

            _logger.LogWarning(
                "Central config at {Url} is unreachable — running from the cached copy fetched {FetchedAt:u}. "
                + "Polling continues; the next successful fetch replaces it.",
                url, cached.Value.Meta.FetchedAtUtc);
        }

        var reloader = new ConfigReloader(
            new CentralConfigSource(_options, _cache, _logger), _registry, _connections, _logger);

        var applied = await reloader.ReloadAsync(cancellationToken);
        if (applied is null)
        {
            trigger.Dispose();
            throw new InvalidOperationException(
                $"Cannot start: the cached central config for {url} could not be loaded "
                + "(see the log above). Fix the served config or clear ~/.mcpOrchestrator/central-config-cache.json.");
        }

        trigger.Start(() => reloader.ReloadAsync(CancellationToken.None));
        _trigger = trigger;

        _logger.LogInformation(
            "Central config active: polling {Url} every ~{Seconds}s with ETag revalidation "
            + "(override with {Variable}).",
            url, (int)_options.PollInterval.TotalSeconds, CentralConfigOptions.PollSecondsVariable);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Dispose();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        _trigger?.Dispose();
        _trigger = null;
    }
}

/// <summary>
/// Central mode's load source: reads the cached payload (written atomically by the poll trigger
/// after validation) and parses it under the central placeholder policy. Startup-from-cache and
/// every applied poll go through this same path.
/// </summary>
internal sealed class CentralConfigSource : IConfigSource
{
    private static readonly IReadOnlyDictionary<string, string> NoBuiltins = new Dictionary<string, string>();

    private readonly CentralConfigOptions _options;
    private readonly CentralConfigCache _cache;
    private readonly ILogger _logger;

    public CentralConfigSource(CentralConfigOptions options, CentralConfigCache cache, ILogger logger)
    {
        _options = options;
        _cache = cache;
        _logger = logger;
    }

    public string Description => _options.Url.ToString();

    public Task<ReloadedConfig?> TryLoadAsync(CancellationToken cancellationToken)
    {
        var cached = _cache.TryRead(_options.Url.ToString());
        if (cached is null)
        {
            _logger.LogError(
                "Central config cache for {Url} is missing or unreadable; keeping the current config.", _options.Url);
            return Task.FromResult<ReloadedConfig?>(null);
        }

        return Task.FromResult(CapabilityCatalog.TryParseForReload(
            cached.Value.Payload, Description, NoBuiltins, forbidLocalPlaceholders: true, _logger));
    }
}
