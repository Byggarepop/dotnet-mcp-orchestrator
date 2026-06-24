using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Profiling;

// ----- Session trace input format (session.jsonl) -------------------------------------------
//
// One JSON object per line, one per turn. The orchestrator can emit this as a side-channel log
// (see ISessionTraceWriter / --trace-out); it is also easy to hand-author for tests. The shape is
// deliberately minimal — turn index plus the route/discover events that turn — because that is all
// the profiler needs to reconstruct which manifests were resident. Manifest *sizes* come from
// measuring the config (profile --trace <file> --config <config>), not from the trace itself.
//
//   {"turn": 3, "events": [{"type": "discover_tools", "capability": "github", "tool": null},
//                          {"type": "route", "capability": "github", "tool": "create_issue"}]}
//
// Blank lines, lines beginning with `//`, and a leading {"type":"header", ...} line are ignored.

/// <summary>One route/discover event within a turn: which capability, and (for route) which tool.</summary>
public sealed class TraceEvent
{
    /// <summary><c>discover_tools</c> or <c>route</c> — the meta-tool the agent invoked.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>The downstream capability the event touched.</summary>
    public string Capability { get; set; } = string.Empty;

    /// <summary>The downstream tool, for <c>route</c> events. Null for <c>discover_tools</c>.</summary>
    public string? Tool { get; set; }
}

/// <summary>One line of a session trace: a turn and the events that happened in it.</summary>
public sealed class TraceLine
{
    /// <summary>Marks a non-turn line (e.g. <c>"header"</c>), which the parser skips.</summary>
    public string? Type { get; set; }

    /// <summary>The turn index. Null/0 falls back to the line's ordinal position.</summary>
    public int? Turn { get; set; }

    /// <summary>The route/discover events that occurred in this turn (may be empty for an idle turn).</summary>
    public List<TraceEvent> Events { get; set; } = new();
}

/// <summary>Source-gen JSON context for reading session traces (AOT/trim-safe, case-insensitive).</summary>
[JsonSourceGenerationOptions(PropertyNameCaseInsensitive = true, AllowTrailingCommas = true, ReadCommentHandling = JsonCommentHandling.Skip)]
[JsonSerializable(typeof(TraceLine))]
internal sealed partial class TraceJsonContext : JsonSerializerContext;

/// <summary>Parses a <c>session.jsonl</c> trace into ordered turns.</summary>
public static class SessionTrace
{
    /// <summary>Reads and parses a trace file.</summary>
    public static IReadOnlyList<TraceLine> ParseFile(string path) => ParseLines(File.ReadLines(path));

    /// <summary>
    /// Parses trace lines. Blank lines, <c>//</c> comments, and a leading header line
    /// (<c>{"type":"header"}</c>) are skipped. Throws <see cref="FormatException"/> on a malformed
    /// data line so a bad trace fails loudly rather than silently producing wrong numbers.
    /// </summary>
    public static IReadOnlyList<TraceLine> ParseLines(IEnumerable<string> lines)
    {
        var turns = new List<TraceLine>();
        var lineNumber = 0;

        foreach (var raw in lines)
        {
            lineNumber++;
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
            {
                continue;
            }

            TraceLine? parsed;
            try
            {
                parsed = JsonSerializer.Deserialize(line, TraceJsonContext.Default.TraceLine);
            }
            catch (JsonException ex)
            {
                throw new FormatException($"Malformed session trace at line {lineNumber}: {ex.Message}", ex);
            }

            if (parsed is null || string.Equals(parsed.Type, "header", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            turns.Add(parsed);
        }

        return turns;
    }
}
