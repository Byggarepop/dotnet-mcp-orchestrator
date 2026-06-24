using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Profiling;

// The machine-readable profile (`--format json`). A clean superset of the human tables:
// everything the table shows is derivable from this. Mode-irrelevant blocks are present-but-null
// (e.g. `envelope` is null in trace mode, `trace`/`summary` are null in static mode) so the schema
// is stable for a CI check to bind to. `orchestrator_favorable` and `break_even_turn` are the
// fields a PR gate asserts on; `variance` is the hook for study mode (replay N → populate cv_pct).

/// <summary>The tokenizer used, disclosed with every report (honesty requirement).</summary>
public sealed record TokenizerInfo(string Name, string Approximates, int CrossModelTolerancePct);

/// <summary>Top-level counts for the profiled config/session.</summary>
public sealed record ConfigInfo(int ServersConnected, int? ServersTouched, int ToolsTotal);

/// <summary>The resting floor: what the agent pays before any work happens.</summary>
public sealed record RestingState(int SystemPromptTokens, int MetaToolsTokens, int FloorTokensPerTurn);

/// <summary>One server's contribution to the naive baseline.</summary>
public sealed record ServerTokens(string Server, int Tools, int Tokens);

/// <summary>The flat "load everything every turn" baseline the orchestrator is measured against.</summary>
public sealed record NaiveBaseline(int TotalTokensPerTurn, IReadOnlyList<ServerTokens> ByServer);

/// <summary>Static envelope: best (nothing routed) and worst (everything routed) per turn.</summary>
public sealed record EnvelopeInfo(int BestCaseTokensPerTurn, int WorstCaseTokensPerTurn, int NaiveTokensPerTurn);

/// <summary>One row of the realized per-turn curve.</summary>
public sealed record TurnRow(int Turn, IReadOnlyList<string> Loaded, int ActiveTokens, int NaiveTokens, int SavedTokens);

/// <summary>A manifest's first load and what triggered it.</summary>
public sealed record LoadEventRow(int Turn, string Server, string Trigger, string? Tool);

/// <summary>The quiet kill-shot: servers never routed to, and the manifest tokens never paid.</summary>
public sealed record NeverLoadedInfo(int Servers, int UnpaidManifestTokens);

/// <summary>The trace-mode block: realized curve, load events, never-loaded savings.</summary>
public sealed record TraceInfo(
    IReadOnlyList<TurnRow> Turns,
    IReadOnlyList<LoadEventRow> LoadEvents,
    NeverLoadedInfo NeverLoaded);

/// <summary>Session totals and the CI-assertable verdict.</summary>
public sealed record SummaryInfo(
    long CumulativeOrchestratedTokens,
    long CumulativeNaiveTokens,
    long NetSavedTokens,
    double NetSavedPct,
    int? BreakEvenTurn,
    bool OrchestratorFavorable);

/// <summary>Study-mode hook: with runs &gt; 1, populate <see cref="CvPct"/> across replays.</summary>
public sealed record VarianceInfo(int Runs, double? CvPct);

/// <summary>The complete profile report, serialized by <c>--format json</c>.</summary>
public sealed record ProfileReport
{
    /// <summary>Schema version of this JSON document.</summary>
    public string SchemaVersion { get; init; } = "1.0";

    /// <summary><c>static</c> or <c>trace</c>.</summary>
    public required string Mode { get; init; }

    /// <summary>The tokenizer used and its cross-model tolerance.</summary>
    public required TokenizerInfo Tokenizer { get; init; }

    /// <summary>Server/tool counts for the profiled config.</summary>
    public required ConfigInfo Config { get; init; }

    /// <summary>The resting floor breakdown.</summary>
    public required RestingState RestingState { get; init; }

    /// <summary>The naive (flat) baseline, per server.</summary>
    public required NaiveBaseline NaiveBaseline { get; init; }

    /// <summary>Static-mode envelope. Null in trace mode.</summary>
    public EnvelopeInfo? Envelope { get; init; }

    /// <summary>Trace-mode realized curve. Null in static mode.</summary>
    public TraceInfo? Trace { get; init; }

    /// <summary>Trace-mode session totals. Null in static mode.</summary>
    public SummaryInfo? Summary { get; init; }

    /// <summary>Trace-mode variance/study hook. Null in static mode.</summary>
    public VarianceInfo? Variance { get; init; }

    /// <summary>
    /// Servers the profiler could not connect to or size. Their manifests are counted as 0 and
    /// excluded from the baseline — disclosed here so the numbers are never silently optimistic.
    /// </summary>
    public IReadOnlyList<string>? UnreachableServers { get; init; }

    /// <summary>Serializes this report as indented, snake_case JSON (AOT/trim-safe).</summary>
    public string ToJson() => JsonSerializer.Serialize(this, ProfileJsonContext.Default.ProfileReport);
}

/// <summary>Source-gen JSON context for the profile report — snake_case, indented, nulls preserved.</summary>
[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProfileReport))]
internal sealed partial class ProfileJsonContext : JsonSerializerContext;
