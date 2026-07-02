namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// A pluggable source of "the config may have changed" signals. The reload pipeline is
/// trigger → load + validate → diff + apply; only this first stage varies by deployment:
/// today a file watcher (<see cref="ConfigFileWatchTrigger"/>), later e.g. polling a URL
/// for a centrally managed config. Triggers only signal — loading, validation and
/// last-known-good handling all live downstream in <see cref="ConfigReloader"/>.
/// </summary>
internal interface IConfigReloadTrigger : IDisposable
{
    /// <summary>
    /// Starts signalling. <paramref name="onChanged"/> is invoked (never concurrently with
    /// itself from the same trigger) each time a change is detected; it must not throw.
    /// </summary>
    void Start(Func<Task> onChanged);
}
