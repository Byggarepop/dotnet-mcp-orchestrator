using System.Text;
using System.Text.Json;

namespace McpOrchestrator.Profiling;

/// <summary>
/// Records the orchestrator's route/discover interactions as a side-channel session trace, so a
/// real run can later be replayed by <c>profile --trace</c>. Off by default
/// (<see cref="NullSessionTraceWriter"/>); enabled with <c>--trace-out &lt;path&gt;</c> or the
/// <c>MCP_ORCHESTRATOR_TRACE_OUT</c> environment variable. The default server hot path never
/// instantiates a tokenizer — the trace stores only the trajectory; sizes are computed at replay.
/// </summary>
public interface ISessionTraceWriter
{
    /// <summary>
    /// Records one interaction. <paramref name="eventType"/> is <c>discover_tools</c> or
    /// <c>route</c>; <paramref name="tool"/> is the downstream tool for a route, else null.
    /// </summary>
    void Record(string eventType, string capability, string? tool);
}

/// <summary>The no-op writer used when tracing is off. Zero overhead.</summary>
public sealed class NullSessionTraceWriter : ISessionTraceWriter
{
    /// <summary>Shared instance.</summary>
    public static readonly NullSessionTraceWriter Instance = new();

    /// <inheritdoc />
    public void Record(string eventType, string capability, string? tool) { }
}

/// <summary>
/// Appends one JSONL line per interaction to a file. A "turn" here is one orchestrator
/// interaction (the points where context actually changes) — the orchestrator cannot observe agent
/// turns in which it is not called, so purely-idle turns are not sampled. Each line is flushed
/// immediately so a hard kill still leaves a complete, replayable trace. Built with
/// <see cref="Utf8JsonWriter"/> — no reflection, AOT/trim-safe.
/// </summary>
public sealed class JsonlSessionTraceWriter : ISessionTraceWriter, IDisposable
{
    private readonly object _gate = new();
    private readonly FileStream _stream;
    private int _turn;
    private bool _disposed;

    /// <summary>Opens (truncating) the trace file at <paramref name="path"/> and writes the header.</summary>
    public JsonlSessionTraceWriter(string path)
    {
        var full = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _stream = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read);
        WriteHeaderLine();
    }

    /// <summary>The absolute path being written.</summary>
    public string FilePath => _stream.Name;

    private void WriteHeaderLine()
    {
        // type:"header" so the replay parser skips it. Documents how to replay the file.
        const string header =
            "{\"type\":\"header\",\"schema\":\"mcp-orchestrator-session-trace/1\"," +
            "\"note\":\"one line per orchestrator interaction (turn = interaction index); replay with: " +
            "mcp-orchestrator profile --trace <this-file> --config <orchestrator-config>\"}\n";
        var bytes = Encoding.UTF8.GetBytes(header);
        _stream.Write(bytes, 0, bytes.Length);
        _stream.Flush();
    }

    /// <inheritdoc />
    public void Record(string eventType, string capability, string? tool)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            var turn = ++_turn;

            using var buffer = new MemoryStream(128);
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartObject();
                writer.WriteNumber("turn", turn);
                writer.WriteStartArray("events");
                writer.WriteStartObject();
                writer.WriteString("type", eventType);
                writer.WriteString("capability", capability);
                if (tool is null)
                {
                    writer.WriteNull("tool");
                }
                else
                {
                    writer.WriteString("tool", tool);
                }
                writer.WriteEndObject();
                writer.WriteEndArray();
                writer.WriteEndObject();
            }

            buffer.Position = 0;
            buffer.WriteTo(_stream);
            _stream.WriteByte((byte)'\n');
            _stream.Flush();
        }
    }

    /// <summary>Flushes and closes the trace file.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stream.Dispose();
        }
    }
}
