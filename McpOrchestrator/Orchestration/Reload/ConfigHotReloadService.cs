using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Hosts config hot reload for the server's lifetime: composes the file-watch trigger with the
/// <see cref="ConfigReloader"/> pipeline. On by default; opt out with
/// <c>MCP_ORCHESTRATOR_NO_RELOAD=1</c> (e.g. when the config lives on a filesystem with unreliable
/// change notifications). Inactive when the server started without a config file — there is
/// nothing to watch, and creating the file still requires a restart.
/// </summary>
internal sealed class ConfigHotReloadService : IHostedService, IDisposable
{
    private readonly CapabilityRegistry _registry;
    private readonly IDownstreamConnectionLifecycle _connections;
    private readonly ILogger<ConfigHotReloadService> _logger;
    private IConfigReloadTrigger? _trigger;

    public ConfigHotReloadService(
        CapabilityRegistry registry,
        IDownstreamConnectionLifecycle connections,
        ILogger<ConfigHotReloadService> logger)
    {
        _registry = registry;
        _connections = connections;
        _logger = logger;
    }

    /// <summary>True when the user opted out via <c>MCP_ORCHESTRATOR_NO_RELOAD=1</c>.</summary>
    internal static bool IsDisabledByEnvironment =>
        Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_NO_RELOAD") == "1";

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsDisabledByEnvironment)
        {
            _logger.LogInformation("Config hot reload disabled (MCP_ORCHESTRATOR_NO_RELOAD=1).");
            return Task.CompletedTask;
        }

        var configPath = _registry.Current.SourcePath;
        if (configPath is null)
        {
            _logger.LogInformation("Config hot reload inactive: the server started without a config file.");
            return Task.CompletedTask;
        }

        var reloader = new ConfigReloader(new FileConfigSource(configPath, _logger), _registry, _connections, _logger);
        _trigger = new ConfigFileWatchTrigger(configPath);
        _trigger.Start(() => reloader.ReloadAsync(CancellationToken.None));

        _logger.LogInformation(
            "Config hot reload active: watching {ConfigPath} (disable with MCP_ORCHESTRATOR_NO_RELOAD=1).",
            configPath);
        return Task.CompletedTask;
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
