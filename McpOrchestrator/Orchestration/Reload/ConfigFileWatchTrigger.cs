namespace McpOrchestrator.Orchestration.Reload;

/// <summary>
/// Signals a reload when the config file changes on disk. Watches the file's <em>directory</em>
/// (filtered to the config filename) for Changed, Created and Renamed — editors and tools often
/// write via temp-file + atomic rename, which raises no Changed on the target path. Events are
/// debounced: the callback fires only after a quiet period with no further events, so an editor's
/// burst of writes coalesces into one reload.
/// </summary>
internal sealed class ConfigFileWatchTrigger : IConfigReloadTrigger
{
    /// <summary>How long the file must stay quiet after an event before the reload fires.</summary>
    public static readonly TimeSpan DefaultQuietPeriod = TimeSpan.FromMilliseconds(500);

    private readonly string _configPath;
    private readonly TimeSpan _quietPeriod;
    private FileSystemWatcher? _watcher;
    private Timer? _debounce;
    private Func<Task>? _onChanged;

    /// <param name="quietPeriod">Debounce window; tests pass a shorter one to stay fast.</param>
    public ConfigFileWatchTrigger(string configPath, TimeSpan? quietPeriod = null)
    {
        _configPath = Path.GetFullPath(configPath);
        _quietPeriod = quietPeriod ?? DefaultQuietPeriod;
    }

    /// <inheritdoc />
    public void Start(Func<Task> onChanged)
    {
        _onChanged = onChanged;

        // The timer is armed (rearmed) by every filesystem event and fires once the burst stops.
        _debounce = new Timer(_ => Fire(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        var directory = Path.GetDirectoryName(_configPath);
        _watcher = new FileSystemWatcher(string.IsNullOrEmpty(directory) ? "." : directory)
        {
            Filter = Path.GetFileName(_configPath),
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
        };
        _watcher.Changed += (_, _) => Poke();
        _watcher.Created += (_, _) => Poke();
        _watcher.Renamed += (_, _) => Poke();
        _watcher.EnableRaisingEvents = true;
    }

    private void Poke() => _debounce?.Change(_quietPeriod, Timeout.InfiniteTimeSpan);

    private void Fire()
    {
        var onChanged = _onChanged;
        if (onChanged is null)
        {
            return;
        }

        // Fire-and-forget off the timer thread; the reloader owns all error handling and logging
        // (an unobserved throw here would kill nothing visibly and stop future reloads silently).
        _ = InvokeSafelyAsync(onChanged);
    }

    private static async Task InvokeSafelyAsync(Func<Task> onChanged)
    {
        try
        {
            await onChanged();
        }
        catch
        {
            // The reloader logs its own failures; a defective callback must not take the timer down.
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _debounce?.Dispose();
    }
}
