namespace McpOrchestrator.Profiling;

/// <summary>One row of the realized per-turn curve.</summary>
/// <param name="Turn">The turn index from the trace.</param>
/// <param name="LoadedThisTurn">Capabilities whose manifest first became resident this turn.</param>
/// <param name="ActiveTokens">Cumulative resident tokens: floor + every manifest loaded so far (sticky).</param>
/// <param name="NaiveTokens">The flat baseline paid every turn (sum of all manifests).</param>
/// <param name="SavedTokens"><c>NaiveTokens - ActiveTokens</c> — shrinks as more servers load.</param>
public sealed record ReplayTurn(
    int Turn,
    IReadOnlyList<string> LoadedThisTurn,
    int ActiveTokens,
    int NaiveTokens,
    int SavedTokens);

/// <summary>The first time a capability's manifest was pulled into context, and what triggered it.</summary>
public sealed record ReplayLoadEvent(int Turn, string Server, string Trigger, string? Tool);

/// <summary>The full realized profile of one replayed session.</summary>
public sealed record ReplayResult(
    IReadOnlyList<ReplayTurn> Turns,
    IReadOnlyList<ReplayLoadEvent> LoadEvents,
    IReadOnlyList<string> TouchedServers,
    IReadOnlyList<string> NeverLoadedServers,
    int NeverLoadedUnpaidTokens,
    long CumulativeOrchestratedTokens,
    long CumulativeNaiveTokens,
    long NetSavedTokens,
    double NetSavedPct,
    int? BreakEvenTurn,
    bool Favorable,
    IReadOnlyList<string> UnknownServers);

/// <summary>
/// Replays a parsed session against measured manifest sizes to produce the realized curve. Models
/// the orchestrator's actual <b>sticky</b> behaviour: a manifest, once pulled in by
/// <c>discover_tools</c>, stays resident and is paid every subsequent turn — the orchestrator has no
/// mechanism to retract a manifest from the agent's context. (If eviction were ever added, this is
/// the one place that would gain a non-monotonic active curve; see the spec's open decision 1.)
/// </summary>
public static class TraceReplay
{
    /// <summary>
    /// Computes the per-turn curve, load events, never-loaded savings, cumulative totals, and
    /// break-even from a trajectory (<paramref name="turns"/>) and the measured server manifests.
    /// </summary>
    public static ReplayResult Run(
        IReadOnlyList<TraceLine> turns,
        IReadOnlyList<ServerManifest> manifests,
        int floorTokens)
    {
        // Universe = every configured server (the never-loaded accounting needs the full set).
        var sizeOf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var m in manifests)
        {
            sizeOf[m.Name] = m.ManifestTokens;
        }

        var naiveTokens = manifests.Sum(m => m.ManifestTokens);

        var resident = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var touched = new List<string>();
        var unknown = new List<string>();
        var rows = new List<ReplayTurn>();
        var loadEvents = new List<ReplayLoadEvent>();

        long cumulativeOrchestrated = 0;
        long cumulativeNaive = 0;
        int? breakEvenTurn = null;

        for (var i = 0; i < turns.Count; i++)
        {
            var line = turns[i];
            var turn = line.Turn is > 0 ? line.Turn.Value : i + 1;
            var loadedThisTurn = new List<string>();

            foreach (var ev in line.Events)
            {
                var cap = ev.Capability;
                if (string.IsNullOrWhiteSpace(cap))
                {
                    continue;
                }

                if (!sizeOf.ContainsKey(cap) && !unknown.Contains(cap, StringComparer.OrdinalIgnoreCase))
                {
                    // Referenced by the trace but absent from the measured config — its size is
                    // unknown, so it contributes 0 to active. Surfaced rather than hidden.
                    unknown.Add(cap);
                }

                if (resident.Add(cap))
                {
                    loadedThisTurn.Add(cap);
                    touched.Add(cap);
                    loadEvents.Add(new ReplayLoadEvent(turn, cap, ev.Type, ev.Tool));
                }
            }

            var active = floorTokens + resident.Sum(r => sizeOf.TryGetValue(r, out var t) ? t : 0);
            rows.Add(new ReplayTurn(turn, loadedThisTurn, active, naiveTokens, naiveTokens - active));

            cumulativeOrchestrated += active;
            cumulativeNaive += naiveTokens;
            if (breakEvenTurn is null && cumulativeNaive - cumulativeOrchestrated > 0)
            {
                breakEvenTurn = turn;
            }
        }

        var netSaved = cumulativeNaive - cumulativeOrchestrated;
        var favorable = netSaved > 0;
        if (!favorable)
        {
            // The session never came out ahead overall — "overhead never repaid". Reporting null
            // (rather than an early, later-reversed crossing) keeps break-even honest.
            breakEvenTurn = null;
        }

        var neverLoaded = manifests
            .Where(m => !resident.Contains(m.Name))
            .Select(m => m.Name)
            .ToList();
        var neverLoadedUnpaid = manifests
            .Where(m => !resident.Contains(m.Name))
            .Sum(m => m.ManifestTokens);

        var netSavedPct = cumulativeNaive > 0
            ? Math.Round(netSaved * 100.0 / cumulativeNaive, 1)
            : 0.0;

        return new ReplayResult(
            Turns: rows,
            LoadEvents: loadEvents,
            TouchedServers: touched,
            NeverLoadedServers: neverLoaded,
            NeverLoadedUnpaidTokens: neverLoadedUnpaid,
            CumulativeOrchestratedTokens: cumulativeOrchestrated,
            CumulativeNaiveTokens: cumulativeNaive,
            NetSavedTokens: netSaved,
            NetSavedPct: netSavedPct,
            BreakEvenTurn: breakEvenTurn,
            Favorable: favorable,
            UnknownServers: unknown);
    }
}
