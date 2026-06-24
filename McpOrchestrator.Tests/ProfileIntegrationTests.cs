using McpOrchestrator.Orchestration;
using McpOrchestrator.Profiling;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Integration tests for the profiler against the real demo downstream: manifest measurement,
/// the full static and trace profilers (which connect to each server), and the runtime
/// <c>--trace-out</c> writer round-tripping back through the replay parser.
/// </summary>
[Trait("Category", "Integration")]
public sealed class ProfileIntegrationTests
{
    private static readonly ITokenCounter Counter = new Cl100kTokenCounter();

    [Fact]
    public async Task ManifestMeasurer_sizes_reachable_servers_and_flags_unreachable()
    {
        var catalog = Demo.Catalog(
            Demo.Capability("jira", "jira"),
            new CapabilityDescriptor
            {
                Name = "broken",
                Summary = "Intentionally unlaunchable.",
                Enabled = true,
                Transport = "stdio",
                Command = "this-command-does-not-exist-xyz",
                ConnectTimeoutSeconds = 5,
            });

        await using var conn = new DownstreamConnectionManager(
            catalog, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());

        var manifests = await ManifestMeasurer.MeasureAsync(catalog, conn, Counter, CancellationToken.None);

        var jira = manifests.Single(m => m.Name == "jira");
        Assert.True(jira.Reachable);
        Assert.True(jira.Tools > 0);
        Assert.True(jira.ManifestTokens > 0);

        var broken = manifests.Single(m => m.Name == "broken");
        Assert.False(broken.Reachable);
        Assert.Equal(0, broken.ManifestTokens);
        Assert.False(string.IsNullOrEmpty(broken.Error));
    }

    [Fact]
    public async Task StaticProfiler_reports_floor_baseline_and_a_worst_case_above_naive()
    {
        var config = WriteDemoConfig();

        var report = await StaticProfiler.RunAsync(config, Counter, CancellationToken.None);

        Assert.Equal("static", report.Mode);
        Assert.Equal(3, report.Config.ServersConnected);
        Assert.True(report.RestingState.FloorTokensPerTurn > 0);
        Assert.Equal(report.RestingState.SystemPromptTokens + report.RestingState.MetaToolsTokens,
            report.RestingState.FloorTokensPerTurn);

        // Every demo server is reachable, so the baseline has all three.
        Assert.Null(report.UnreachableServers);
        Assert.Equal(3, report.NaiveBaseline.ByServer.Count);

        // The credibility check: orchestrated worst case is HIGHER than naive (floor + everything).
        Assert.NotNull(report.Envelope);
        Assert.Equal(report.RestingState.FloorTokensPerTurn + report.NaiveBaseline.TotalTokensPerTurn,
            report.Envelope!.WorstCaseTokensPerTurn);
        Assert.True(report.Envelope.WorstCaseTokensPerTurn > report.Envelope.NaiveTokensPerTurn);
    }

    [Fact]
    public async Task TraceProfiler_replays_a_session_against_a_measured_config()
    {
        var config = WriteDemoConfig();
        var trace = WriteTempFile("session", ".jsonl", string.Join('\n',
            """{"turn":1,"events":[{"type":"discover_tools","capability":"jira","tool":null}]}""",
            """{"turn":2,"events":[{"type":"route","capability":"jira","tool":"get_issue"}]}""",
            """{"turn":3,"events":[{"type":"discover_tools","capability":"codegen","tool":null}]}"""));

        var report = await TraceProfiler.RunAsync(trace, config, Counter, CancellationToken.None);

        Assert.Equal("trace", report.Mode);
        Assert.NotNull(report.Trace);
        Assert.NotNull(report.Summary);

        // jira + codegen touched; diag never loaded.
        Assert.Equal(2, report.Config.ServersTouched);
        Assert.Equal(1, report.Trace!.NeverLoaded.Servers);

        // Internal consistency: net = cumulative naive - cumulative orchestrated.
        var s = report.Summary!;
        Assert.Equal(s.CumulativeNaiveTokens - s.CumulativeOrchestratedTokens, s.NetSavedTokens);
        Assert.Equal(s.NetSavedTokens > 0, s.OrchestratorFavorable);
    }

    [Fact]
    public async Task TraceOut_writer_produces_a_file_that_replays()
    {
        var tracePath = WriteTempFile("trace-out", ".jsonl", string.Empty);
        var catalog = Demo.Catalog(Demo.Capability("jira", "jira"));

        using (var writer = new JsonlSessionTraceWriter(tracePath))
        await using (var conn = new DownstreamConnectionManager(
            catalog, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>(), writer))
        {
            await conn.ListToolsAsync("jira", CancellationToken.None);
            await conn.CallToolAsync("jira", "get_issue",
                new Dictionary<string, object?> { ["issueKey"] = "PROJ-1" }, CancellationToken.None);
        }

        var turns = SessionTrace.ParseFile(tracePath);

        Assert.Equal(2, turns.Count);
        Assert.Equal("discover_tools", turns[0].Events[0].Type);
        Assert.Equal("jira", turns[0].Events[0].Capability);
        Assert.Equal("route", turns[1].Events[0].Type);
        Assert.Equal("get_issue", turns[1].Events[0].Tool);

        // And the replay engine accepts it end-to-end.
        var manifests = new[] { new ServerManifest("jira", 2, 200, true, null) };
        var replay = TraceReplay.Run(turns, manifests, floorTokens: 100);
        Assert.Single(replay.LoadEvents);
        Assert.Equal("jira", replay.LoadEvents[0].Server);
    }

    [Fact]
    public async Task TraceOut_writer_is_injected_into_the_connection_manager_via_DI()
    {
        // Mirrors how OrchestratorHost wires --trace-out: the writer is a registered singleton and
        // the connection manager picks it up through its OPTIONAL constructor parameter. If DI used
        // the default (null) instead of the registered writer, --trace-out would silently no-op —
        // this test guards exactly that seam.
        var spy = new SpyTraceWriter();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<ICapabilityCatalog>(Demo.Catalog(Demo.Capability("jira", "jira")));
        services.AddSingleton<IDownstreamConnectionManager, DownstreamConnectionManager>();
        services.AddSingleton<ISessionTraceWriter>(spy);

        await using var provider = services.BuildServiceProvider();
        var connections = provider.GetRequiredService<IDownstreamConnectionManager>();
        await connections.ListToolsAsync("jira", CancellationToken.None);

        Assert.Contains(spy.Events, e => e is ("discover_tools", "jira", null));
    }

    [Fact]
    public async Task Profile_from_an_imported_host_config_measures_stdio_and_skips_remote()
    {
        // The exact wiring `profile --host-config` uses: parse a host config, import its stdio
        // servers into a catalog (FromDescriptors), then run the static profiler against it.
        var demo = Demo.DemoDll.Replace('\\', '/');
        var import = McpOrchestrator.Setup.HostConfigImport.Parse($$"""
        {
          "mcpServers": {
            "jira":    { "command": "dotnet", "args": ["{{demo}}", "--persona", "jira"] },
            "codegen": { "command": "dotnet", "args": ["{{demo}}", "--persona", "codegen"] },
            "remote":  { "type": "http", "url": "https://example.com/mcp" }
          }
        }
        """);

        Assert.Equal(new[] { "jira", "codegen" }, import.Imported);
        Assert.Equal(new[] { "remote" }, import.SkippedRemote);

        var catalog = CapabilityCatalog.FromDescriptors(import.Capabilities, NullLogger.Instance);
        var report = await StaticProfiler.RunAsync(catalog, Counter, CancellationToken.None);

        Assert.Equal("static", report.Mode);
        Assert.Equal(2, report.Config.ServersConnected);
        Assert.Equal(2, report.NaiveBaseline.ByServer.Count);
        Assert.True(report.NaiveBaseline.TotalTokensPerTurn > 0);
        Assert.Null(report.UnreachableServers);
    }

    private sealed class SpyTraceWriter : ISessionTraceWriter
    {
        public List<(string Type, string Capability, string? Tool)> Events { get; } = new();

        public void Record(string eventType, string capability, string? tool) =>
            Events.Add((eventType, capability, tool));
    }

    // ----- helpers ----------------------------------------------------------------------------

    /// <summary>Writes a temp orchestrator config that launches the built demo server as three personas.</summary>
    private static string WriteDemoConfig()
    {
        var demo = Demo.DemoDll.Replace('\\', '/');
        var json = $$"""
        {
          "capabilities": [
            { "name": "jira",    "summary": "Issue tracking.",  "enabled": true, "command": "dotnet", "args": ["{{demo}}", "--persona", "jira"] },
            { "name": "codegen", "summary": "Code generation.", "enabled": true, "command": "dotnet", "args": ["{{demo}}", "--persona", "codegen"] },
            { "name": "diag",    "summary": "Diagnostics.",     "enabled": true, "command": "dotnet", "args": ["{{demo}}", "--persona", "diag"] }
          ]
        }
        """;
        return WriteTempFile("orch-config", ".json", json);
    }

    private static string WriteTempFile(string prefix, string extension, string content)
    {
        var path = Path.Combine(Path.GetTempPath(), $"{prefix}-{Guid.NewGuid():N}{extension}");
        File.WriteAllText(path, content);
        return path;
    }
}
