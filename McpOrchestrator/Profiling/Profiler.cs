using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpOrchestrator.Profiling;

/// <summary>Shared measurement step: load the config, measure the floor, and size every manifest.</summary>
internal static class Profiler
{
    /// <summary>The pieces both modes need: the catalog, the resting floor, and per-server manifests.</summary>
    internal sealed record Measurement(
        ICapabilityCatalog Catalog,
        FloorBreakdown Floor,
        IReadOnlyList<ServerManifest> Manifests);

    /// <summary>
    /// Loads the config and measures the floor and every server's manifest. Connecting to each
    /// downstream server is the unavoidable cost of an accurate size — this is what "static, no
    /// session" means: the numbers come from the servers, not from guessing.
    /// </summary>
    internal static async Task<Measurement> MeasureAsync(
        string configPath, ITokenCounter counter, CancellationToken cancellationToken)
    {
        var catalog = CapabilityCatalog.LoadFromFile(configPath, NullLogger.Instance);
        var floor = OrchestratorSurface.MeasureFloor(counter);

        await using var connections = new DownstreamConnectionManager(
            catalog, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());
        var manifests = await ManifestMeasurer.MeasureAsync(catalog, connections, counter, cancellationToken);

        return new Measurement(catalog, floor, manifests);
    }

    internal static TokenizerInfo Disclose(ITokenCounter counter) =>
        new(counter.Name, counter.Approximates, counter.CrossModelTolerancePct);

    /// <summary>Servers sorted biggest-manifest-first, as the human baseline table shows them.</summary>
    internal static List<ServerTokens> ByServerDescending(IEnumerable<ServerManifest> reachable) =>
        reachable
            .OrderByDescending(m => m.ManifestTokens)
            .ThenBy(m => m.Name, StringComparer.Ordinal)
            .Select(m => new ServerTokens(m.Name, m.Tools, m.ManifestTokens))
            .ToList();
}

/// <summary>
/// Static mode (<c>profile --config</c>): resting floor, naive baseline, and the envelope. No
/// session needed — deterministic and CI-friendly, the easiest mode to assert on.
/// </summary>
public static class StaticProfiler
{
    /// <summary>Builds the static profile report for a config file.</summary>
    public static async Task<ProfileReport> RunAsync(
        string configPath, ITokenCounter counter, CancellationToken cancellationToken)
    {
        var m = await Profiler.MeasureAsync(configPath, counter, cancellationToken);

        var reachable = m.Manifests.Where(x => x.Reachable).ToList();
        var unreachable = m.Manifests.Where(x => !x.Reachable).Select(x => x.Name).ToList();
        var naiveTotal = reachable.Sum(x => x.ManifestTokens);

        return new ProfileReport
        {
            Mode = "static",
            Tokenizer = Profiler.Disclose(counter),
            Config = new ConfigInfo(m.Catalog.Capabilities.Count, ServersTouched: null, ToolsTotal: reachable.Sum(x => x.Tools)),
            RestingState = new RestingState(m.Floor.SystemPromptTokens, m.Floor.MetaToolsTokens, m.Floor.FloorTokens),
            NaiveBaseline = new NaiveBaseline(naiveTotal, Profiler.ByServerDescending(reachable)),
            // Worst case is INTENTIONALLY higher than naive: orchestrated worst case means you paid
            // the routing floor AND ended up loading everything. Reporting this honestly is a
            // credibility requirement, not optional.
            Envelope = new EnvelopeInfo(
                BestCaseTokensPerTurn: m.Floor.FloorTokens,
                WorstCaseTokensPerTurn: m.Floor.FloorTokens + naiveTotal,
                NaiveTokensPerTurn: naiveTotal),
            UnreachableServers = unreachable.Count > 0 ? unreachable : null,
        };
    }
}

/// <summary>
/// Trace mode (<c>profile --trace &lt;session.jsonl&gt; --config &lt;config&gt;</c>): replays an
/// actual session into the realized curve — per-turn active vs. naive, load events, never-loaded
/// savings, and break-even. The config supplies manifest sizes; the trace supplies the trajectory.
/// </summary>
public static class TraceProfiler
{
    /// <summary>Builds the trace profile report for a session trace measured against a config.</summary>
    public static async Task<ProfileReport> RunAsync(
        string tracePath, string configPath, ITokenCounter counter, CancellationToken cancellationToken)
    {
        var m = await Profiler.MeasureAsync(configPath, counter, cancellationToken);
        var turns = SessionTrace.ParseFile(tracePath);
        var replay = TraceReplay.Run(turns, m.Manifests, m.Floor.FloorTokens);

        var reachable = m.Manifests.Where(x => x.Reachable).ToList();
        var unreachable = m.Manifests.Where(x => !x.Reachable).Select(x => x.Name);
        var disclose = unreachable.Concat(replay.UnknownServers)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        return new ProfileReport
        {
            Mode = "trace",
            Tokenizer = Profiler.Disclose(counter),
            Config = new ConfigInfo(m.Catalog.Capabilities.Count, replay.TouchedServers.Count, reachable.Sum(x => x.Tools)),
            RestingState = new RestingState(m.Floor.SystemPromptTokens, m.Floor.MetaToolsTokens, m.Floor.FloorTokens),
            NaiveBaseline = new NaiveBaseline(reachable.Sum(x => x.ManifestTokens), Profiler.ByServerDescending(reachable)),
            Trace = new TraceInfo(
                Turns: replay.Turns
                    .Select(t => new TurnRow(t.Turn, t.LoadedThisTurn, t.ActiveTokens, t.NaiveTokens, t.SavedTokens))
                    .ToList(),
                LoadEvents: replay.LoadEvents
                    .Select(e => new LoadEventRow(e.Turn, e.Server, e.Trigger, e.Tool))
                    .ToList(),
                NeverLoaded: new NeverLoadedInfo(replay.NeverLoadedServers.Count, replay.NeverLoadedUnpaidTokens)),
            Summary = new SummaryInfo(
                replay.CumulativeOrchestratedTokens,
                replay.CumulativeNaiveTokens,
                replay.NetSavedTokens,
                replay.NetSavedPct,
                replay.BreakEvenTurn,
                replay.Favorable),
            // A single replay has no run-to-run variance. Study mode (replay N sessions → populate
            // cv_pct, per the routing study's methodology) is the documented extension here.
            Variance = new VarianceInfo(Runs: 1, CvPct: null),
            UnreachableServers = disclose.Count > 0 ? disclose : null,
        };
    }
}
