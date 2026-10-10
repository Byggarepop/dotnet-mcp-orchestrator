using System.Text;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Diagnostics;

/// <summary>
/// A tiny, dependency-free, AOT-safe file logger. It mirrors the orchestrator's stderr log to a
/// file so the flow (config load, connects, tool calls, errors) can be inspected after the fact —
/// stderr from an MCP child process is otherwise easy to lose.
///
/// <para>By default it writes <c>%USERPROFILE%/.mcpOrchestrator/orchestrator.log</c>
/// (the folder is created if missing). Override the directory with
/// <c>MCP_ORCHESTRATOR_LOG_DIR</c>, or disable file logging with
/// <c>MCP_ORCHESTRATOR_LOG_DIR=off</c>. If a second instance already holds the main log, this one
/// falls back to a per-process file so they never contend. Never writes to stdout (reserved for the
/// MCP protocol) and never throws.</para>
/// </summary>
internal sealed class FileLoggerProvider : ILoggerProvider
{
    private const string DirEnv = "MCP_ORCHESTRATOR_LOG_DIR";
    private const string DefaultFolder = ".mcpOrchestrator";
    private const long MaxBytes = 10 * 1024 * 1024; // rotate the active log past ~10 MB

    private readonly object _gate = new();
    private readonly StreamWriter _writer;

    /// <summary>The file this provider is writing to.</summary>
    public string FilePath { get; }

    private FileLoggerProvider(StreamWriter writer, string filePath)
    {
        _writer = writer;
        FilePath = filePath;
    }

    /// <summary>
    /// Resolves the log directory, ensures it exists, and opens the log for append. Returns
    /// <c>null</c> if file logging is disabled or can't be set up (logging must never block startup).
    /// </summary>
    public static FileLoggerProvider? Create(string? directoryOverride = null)
    {
        try
        {
            var dir = directoryOverride ?? Environment.GetEnvironmentVariable(DirEnv);
            if (dir is "off" or "none" or "disabled")
            {
                return null;
            }
            if (string.IsNullOrWhiteSpace(dir))
            {
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), DefaultFolder);
            }

            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, "orchestrator.log");
            RotateIfLarge(path);

            FileStream stream;
            try
            {
                stream = OpenAppend(path);
            }
            catch (IOException)
            {
                // Another instance already owns orchestrator.log (Windows enforces the share
                // mode; on Unix the open succeeds and both instances append to the same file).
                return CreatePerProcess(dir);
            }

            return FromStream(stream, path);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Opens this process's own <c>orchestrator-&lt;pid&gt;.log</c>, truncating it: Windows reuses
    /// process IDs, so an existing file with this PID belongs to a finished run and appending
    /// would make its history look like ours.
    /// </summary>
    internal static FileLoggerProvider CreatePerProcess(string dir)
    {
        var path = Path.Combine(dir, $"orchestrator-{Environment.ProcessId}.log");
        return FromStream(OpenTruncate(path), path);
    }

    private static FileLoggerProvider FromStream(FileStream stream, string path)
    {
        var writer = new StreamWriter(stream) { AutoFlush = true };
        writer.WriteLine(SessionBanner());
        return new FileLoggerProvider(writer, path);
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(categoryName, this);

    public void Dispose()
    {
        lock (_gate)
        {
            _writer.Dispose();
        }
    }

    private void WriteLine(string line)
    {
        try
        {
            lock (_gate)
            {
                _writer.WriteLine(line);
            }
        }
        catch
        {
            // A logging failure must never disrupt the server.
        }
    }

    // FileShare.Read lets you tail the log while it's being written, but blocks a second writer
    // (which then falls back to a per-process file).
    private static FileStream OpenAppend(string path) =>
        new(path, FileMode.Append, FileAccess.Write, FileShare.Read);

    private static FileStream OpenTruncate(string path) =>
        new(path, FileMode.Create, FileAccess.Write, FileShare.Read);

    /// <summary>
    /// One line marking where this process's entries start, so runs appended to the same file
    /// (orchestrator.log is shared across sessions) can be told apart.
    /// </summary>
    internal static string SessionBanner()
    {
        var version = typeof(FileLoggerProvider).Assembly.GetName().Version?.ToString(3) ?? "?";
        return $"===== McpOrchestrator {version} session start {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} PID {Environment.ProcessId} =====";
    }

    private static void RotateIfLarge(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backup = path + ".1";
                if (File.Exists(backup)) File.Delete(backup);
                File.Move(path, backup);
            }
        }
        catch
        {
            // best-effort rotation
        }
    }

    private sealed class FileLogger(string category, FileLoggerProvider provider) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var sb = new StringBuilder();
            sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"))
              .Append(' ').Append(Short(logLevel))
              .Append(' ').Append(category)
              .Append(": ").Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.Append(Environment.NewLine).Append(exception);
            }
            provider.WriteLine(sb.ToString());
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "trce",
            LogLevel.Debug => "dbug",
            LogLevel.Information => "info",
            LogLevel.Warning => "warn",
            LogLevel.Error => "fail",
            LogLevel.Critical => "crit",
            _ => "    ",
        };
    }
}
