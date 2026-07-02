using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Where the reload pipeline's load + validate stage gets the config from. The pipeline is
/// trigger → load + validate → diff + apply; the source pairs with a trigger implementation:
/// the local file (with <see cref="ConfigFileWatchTrigger"/>) or the centrally fetched cache
/// (with <see cref="HttpConfigPollTrigger"/>). A source never throws — a failed or rejected
/// load returns <c>null</c> after logging, and the running config stays live (last-known-good).
/// </summary>
internal interface IConfigSource
{
    /// <summary>Where the config comes from, for log lines — a file path or a URL.</summary>
    string Description { get; }

    /// <summary>Loads and validates the current config, or <c>null</c> when it must be rejected.</summary>
    Task<ReloadedConfig?> TryLoadAsync(CancellationToken cancellationToken);
}

/// <summary>Loads the config from the local file — file mode's source, wrapping the same
/// strict <see cref="CapabilityCatalog.TryLoadForReload"/> used since hot reload shipped.</summary>
internal sealed class FileConfigSource : IConfigSource
{
    private readonly string _configPath;
    private readonly ILogger _logger;

    public FileConfigSource(string configPath, ILogger logger)
    {
        _configPath = configPath;
        _logger = logger;
    }

    public string Description => _configPath;

    public Task<ReloadedConfig?> TryLoadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(CapabilityCatalog.TryLoadForReload(_configPath, _logger));
}
