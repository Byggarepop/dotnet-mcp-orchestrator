using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Tests;

/// <summary>Records which capabilities the reload pipeline retired, without touching processes.</summary>
internal sealed class SpyLifecycle : IDownstreamConnectionLifecycle
{
    private readonly List<string> _invalidated = new();

    public IReadOnlyList<string> Invalidated
    {
        get { lock (_invalidated) { return _invalidated.ToArray(); } }
    }

    public Task InvalidateAsync(string capability, CancellationToken cancellationToken)
    {
        lock (_invalidated)
        {
            _invalidated.Add(capability);
        }
        return Task.CompletedTask;
    }
}

/// <summary>Captures log output so tests can assert on levels and message content.</summary>
internal sealed class CollectingLogger : ILogger
{
    private readonly List<(LogLevel Level, string Message)> _entries = new();

    public IReadOnlyList<(LogLevel Level, string Message)> Entries
    {
        get { lock (_entries) { return _entries.ToArray(); } }
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        lock (_entries)
        {
            _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
