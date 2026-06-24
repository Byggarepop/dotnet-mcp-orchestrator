using McpOrchestrator.Profiling;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Pure unit tests for the profiler: token counting, the resting-floor measurement, the trace
/// replay arithmetic (the differentiator), trace parsing, JSON schema, and rendering. None of
/// these need a downstream server — replay runs against synthetic manifests with hand-verified
/// numbers, so the math is pinned exactly.
/// </summary>
public sealed class ProfilingUnitTests
{
    // ----- token counter ----------------------------------------------------------------------

    [Fact]
    public void TokenCounter_discloses_cl100k_and_tolerance()
    {
        var counter = new Cl100kTokenCounter();
        Assert.Equal("cl100k_base", counter.Name);
        Assert.Equal(10, counter.CrossModelTolerancePct);
        Assert.False(string.IsNullOrWhiteSpace(counter.Approximates));
    }

    [Fact]
    public void TokenCounter_is_zero_for_empty_and_positive_and_deterministic()
    {
        var counter = new Cl100kTokenCounter();
        Assert.Equal(0, counter.Count(null));
        Assert.Equal(0, counter.Count(string.Empty));

        var a = counter.Count("The orchestrator routes one agent to many MCP servers.");
        Assert.True(a > 0);
        Assert.Equal(a, counter.Count("The orchestrator routes one agent to many MCP servers."));
    }

    // ----- resting floor ----------------------------------------------------------------------

    [Fact]
    public void Floor_measures_exactly_the_three_meta_tools()
    {
        var floor = OrchestratorSurface.MeasureFloor(new Cl100kTokenCounter());

        Assert.Equal(3, floor.ToolCount);
        Assert.Equal(
            new[] { "discover_tools", "list_capabilities", "route" },
            floor.Tools.Select(t => t.Name).OrderBy(n => n, StringComparer.Ordinal));

        Assert.True(floor.MetaToolsTokens > 0);
        Assert.Equal(floor.Tools.Sum(t => t.Tokens), floor.MetaToolsTokens);
        // This orchestrator advertises no server-level instructions; its guidance lives in the
        // tool descriptions. So the "system prompt" component is 0 and the floor is the tools.
        Assert.Equal(0, floor.SystemPromptTokens);
        Assert.Equal(floor.MetaToolsTokens, floor.FloorTokens);
    }

    // ----- trace replay: the arithmetic -------------------------------------------------------

    private static readonly IReadOnlyList<ServerManifest> FourServers = new[]
    {
        new ServerManifest("github", 14, 11240, true, null),
        new ServerManifest("postgres", 9, 6180, true, null),
        new ServerManifest("slack", 11, 4920, true, null),
        new ServerManifest("filesystem", 5, 2310, true, null),
    };

    private const int Floor = 473;
    private const int Naive = 11240 + 6180 + 4920 + 2310; // 24,650

    private static TraceLine Turn(int n, params TraceEvent[] events) =>
        new() { Turn = n, Events = events.ToList() };

    private static TraceEvent Discover(string cap) => new() { Type = "discover_tools", Capability = cap };
    private static TraceEvent Route(string cap, string tool) => new() { Type = "route", Capability = cap, Tool = tool };

    [Fact]
    public void Replay_favorable_session_curve_and_breakeven_are_exact()
    {
        var turns = new[]
        {
            Turn(1),
            Turn(2, Discover("github")),
            Turn(3, Route("github", "create_issue")),
            Turn(4, Discover("postgres")),
        };

        var r = TraceReplay.Run(turns, FourServers, Floor);

        // active = floor + Σ resident manifests (sticky); naive = Σ all manifests (flat).
        Assert.Equal(new[] { 473, 11713, 11713, 17893 }, r.Turns.Select(t => t.ActiveTokens));
        Assert.All(r.Turns, t => Assert.Equal(Naive, t.NaiveTokens));
        Assert.Equal(new[] { 24177, 12937, 12937, 6757 }, r.Turns.Select(t => t.SavedTokens));

        // route to an already-resident server is not a second load event.
        Assert.Equal(2, r.LoadEvents.Count);
        Assert.Equal(("github", 2, "discover_tools"), (r.LoadEvents[0].Server, r.LoadEvents[0].Turn, r.LoadEvents[0].Trigger));
        Assert.Equal(("postgres", 4, "discover_tools"), (r.LoadEvents[1].Server, r.LoadEvents[1].Turn, r.LoadEvents[1].Trigger));

        Assert.Equal(41792, r.CumulativeOrchestratedTokens); // 473+11713+11713+17893
        Assert.Equal(98600, r.CumulativeNaiveTokens);        // 24650*4
        Assert.Equal(56808, r.NetSavedTokens);
        Assert.True(r.Favorable);
        Assert.Equal(1, r.BreakEvenTurn);

        // slack + filesystem never touched.
        Assert.Equal(2, r.NeverLoadedServers.Count);
        Assert.Equal(4920 + 2310, r.NeverLoadedUnpaidTokens);
        Assert.Equal(2, r.TouchedServers.Count);
    }

    [Fact]
    public void Replay_unfavorable_session_reports_never_repaid()
    {
        // Touch everything immediately, then idle: active = floor + all > naive every turn.
        var turns = new[]
        {
            Turn(1, Discover("github"), Discover("postgres"), Discover("slack"), Discover("filesystem")),
            Turn(2),
        };

        var r = TraceReplay.Run(turns, FourServers, Floor);

        Assert.False(r.Favorable);
        Assert.Null(r.BreakEvenTurn);
        Assert.True(r.NetSavedTokens < 0);
        Assert.Equal((Floor + Naive) * 2, r.CumulativeOrchestratedTokens);
        Assert.Equal(Naive * 2, r.CumulativeNaiveTokens);
        Assert.Empty(r.NeverLoadedServers);
    }

    [Fact]
    public void Replay_flags_servers_referenced_but_not_in_config()
    {
        var turns = new[] { Turn(1, Discover("ghost")) };

        var r = TraceReplay.Run(turns, FourServers, Floor);

        Assert.Contains("ghost", r.UnknownServers);
        // Unknown server contributes 0 (its size is genuinely unknown), so active stays at floor.
        Assert.Equal(Floor, r.Turns[0].ActiveTokens);
    }

    // ----- trace parsing ----------------------------------------------------------------------

    [Fact]
    public void Parse_skips_comments_blanks_and_header_and_reads_events()
    {
        var lines = new[]
        {
            "// a hand-written trace",
            "",
            """{"type":"header","note":"ignored"}""",
            """{"turn":1,"events":[{"type":"discover_tools","capability":"github","tool":null}]}""",
            """{"turn":2,"events":[]}""",
        };

        var turns = SessionTrace.ParseLines(lines);

        Assert.Equal(2, turns.Count);
        Assert.Equal(1, turns[0].Turn);
        Assert.Equal("github", turns[0].Events[0].Capability);
        Assert.Equal("discover_tools", turns[0].Events[0].Type);
        Assert.Null(turns[0].Events[0].Tool);
        Assert.Empty(turns[1].Events);
    }

    [Fact]
    public void Parse_malformed_line_throws_with_line_number()
    {
        var ex = Assert.Throws<FormatException>(() => SessionTrace.ParseLines(new[] { "{not valid json" }));
        Assert.Contains("line 1", ex.Message);
    }

    // ----- JSON schema ------------------------------------------------------------------------

    [Fact]
    public void Json_static_report_uses_snake_case_superset_with_nulls_preserved()
    {
        var json = MakeStaticReport().ToJson();

        Assert.Contains("\"schema_version\": \"1.0\"", json);
        Assert.Contains("\"floor_tokens_per_turn\"", json);
        Assert.Contains("\"by_server\"", json);
        Assert.Contains("\"worst_case_tokens_per_turn\"", json);
        Assert.Contains("\"cross_model_tolerance_pct\"", json);
        // Mode-irrelevant blocks are present-but-null (stable schema for a CI check).
        Assert.Contains("\"trace\": null", json);
        Assert.Contains("\"summary\": null", json);
    }

    [Fact]
    public void Json_trace_report_exposes_ci_assertable_fields()
    {
        var json = MakeTraceReport(favorable: true).ToJson();

        Assert.Contains("\"break_even_turn\": 1", json);
        Assert.Contains("\"orchestrator_favorable\": true", json);
        Assert.Contains("\"net_saved_pct\"", json);
        Assert.Contains("\"cv_pct\": null", json);
        Assert.Contains("\"unpaid_manifest_tokens\"", json);
    }

    // ----- rendering --------------------------------------------------------------------------

    [Fact]
    public void Render_static_shows_the_envelope_and_worst_above_naive()
    {
        var text = ProfileRenderer.Render(MakeStaticReport());

        Assert.Contains("Token Profile (static)", text);
        Assert.Contains("RESTING STATE", text);
        Assert.Contains("NAIVE BASELINE", text);
        Assert.Contains("ENVELOPE", text);
        Assert.Contains("← flat, paid every turn", text);
    }

    [Fact]
    public void Render_trace_favorable_and_unfavorable_are_both_honest()
    {
        var favorable = ProfileRenderer.Render(MakeTraceReport(favorable: true));
        Assert.Contains("net saved", favorable);
        Assert.Contains("overhead repaid at turn 1", favorable);
        Assert.Contains("never loaded", favorable);

        var unfavorable = ProfileRenderer.Render(MakeTraceReport(favorable: false));
        Assert.Contains("net cost", unfavorable);
        Assert.Contains("overhead never repaid", unfavorable);
        Assert.Contains("wrong choice for this workload", unfavorable);
    }

    // ----- report builders for the JSON/render tests ------------------------------------------

    private static TokenizerInfo Tok => new("cl100k_base", "claude-sonnet", 10);

    private static ProfileReport MakeStaticReport() => new()
    {
        Mode = "static",
        Tokenizer = Tok,
        Config = new ConfigInfo(3, null, 6),
        RestingState = new RestingState(0, 585, 585),
        NaiveBaseline = new NaiveBaseline(702, new List<ServerTokens>
        {
            new("diag", 3, 286), new("jira", 2, 241), new("codegen", 1, 175),
        }),
        Envelope = new EnvelopeInfo(585, 1287, 702),
    };

    private static ProfileReport MakeTraceReport(bool favorable)
    {
        var turns = favorable
            ? new[] { Turn(1), Turn(2, Discover("github")) }
            : new[] { Turn(1, Discover("github"), Discover("postgres"), Discover("slack"), Discover("filesystem")) };

        var replay = TraceReplay.Run(turns, FourServers, Floor);

        return new ProfileReport
        {
            Mode = "trace",
            Tokenizer = Tok,
            Config = new ConfigInfo(4, replay.TouchedServers.Count, 39),
            RestingState = new RestingState(0, Floor, Floor),
            NaiveBaseline = new NaiveBaseline(Naive, FourServers
                .OrderByDescending(m => m.ManifestTokens)
                .Select(m => new ServerTokens(m.Name, m.Tools, m.ManifestTokens)).ToList()),
            Trace = new TraceInfo(
                replay.Turns.Select(t => new TurnRow(t.Turn, t.LoadedThisTurn, t.ActiveTokens, t.NaiveTokens, t.SavedTokens)).ToList(),
                replay.LoadEvents.Select(e => new LoadEventRow(e.Turn, e.Server, e.Trigger, e.Tool)).ToList(),
                new NeverLoadedInfo(replay.NeverLoadedServers.Count, replay.NeverLoadedUnpaidTokens)),
            Summary = new SummaryInfo(
                replay.CumulativeOrchestratedTokens, replay.CumulativeNaiveTokens,
                replay.NetSavedTokens, replay.NetSavedPct, replay.BreakEvenTurn, replay.Favorable),
            Variance = new VarianceInfo(1, null),
        };
    }
}
