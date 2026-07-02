using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Reload;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Hot reload against the real demo downstream: in-flight calls draining across a removal, and
/// the full trigger → load → diff → apply pipeline picking up a config-file edit at runtime.
/// </summary>
[Trait("Category", "Integration")]
public sealed class HotReloadIntegrationTests
{
    [Fact]
    public async Task In_flight_route_completes_while_removal_applies_and_later_routes_fail_as_unknown()
    {
        var registry = new CapabilityRegistry(
            CapabilityCatalog.FromDescriptors(new[] { Demo.Capability("diag", "diag") }, NullLogger.Instance));
        await using var connections = new DownstreamConnectionManager(
            registry, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());
        var reloader = new ConfigReloader(
            new FileConfigSource("unused.json", NullLogger.Instance), registry, connections, NullLogger.Instance);

        // Warm the connection, then park a slow call in flight.
        await connections.CallToolAsync(
            "diag", "echo", new Dictionary<string, object?> { ["message"] = "warm-up" }, CancellationToken.None);
        var inFlight = connections.CallToolAsync(
            "diag", "slow", new Dictionary<string, object?> { ["delayMs"] = 2000 }, CancellationToken.None);
        // Sequencing only (not an assertion): give the slow call time to reach the downstream.
        await Task.Delay(300);

        // Remove the capability while the call is running. Apply blocks on the drain, so run it
        // as a task and observe the swapped registry immediately.
        var empty = new ReloadedConfig(
            CapabilityCatalog.FromDescriptors(Array.Empty<CapabilityDescriptor>(), NullLogger.Instance),
            Array.Empty<CapabilityDescriptor>());
        var applyTask = reloader.ApplyAsync(empty, CancellationToken.None);

        // New routes see the new config at once and fail like any unknown capability...
        await Wait.ForAssertionAsync(() => Assert.Null(registry.Find("diag")));
        await Assert.ThrowsAsync<CapabilityNotFoundException>(() => connections.CallToolAsync(
            "diag", "echo", new Dictionary<string, object?> { ["message"] = "late" }, CancellationToken.None));

        // ...while the in-flight call drains to a normal completion before disposal.
        var result = await inFlight;
        Assert.NotEqual(true, result.IsError);

        var diff = await applyTask;
        Assert.Equal(new[] { "diag" }, diff.Removed);
    }

    [Fact]
    public async Task Editing_the_config_file_updates_list_capabilities_without_a_restart()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"hot-reload-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var configPath = Path.Combine(dir, "orchestrator.config.json");
        await File.WriteAllTextAsync(configPath, DemoConfig("jira"));
        try
        {
            // The same wiring the host uses: registry ← config file, connections + reloader over
            // the registry, file trigger driving the reloader. No restart anywhere below.
            var loaded = CapabilityCatalog.TryLoadForReload(configPath, NullLogger.Instance);
            Assert.NotNull(loaded);
            var registry = new CapabilityRegistry(loaded!.Catalog);
            await using var connections = new DownstreamConnectionManager(
                registry, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());
            var reloader = new ConfigReloader(
                new FileConfigSource(configPath, NullLogger.Instance), registry, connections, NullLogger.Instance);
            using var trigger = new ConfigFileWatchTrigger(configPath, TimeSpan.FromMilliseconds(150));
            trigger.Start(() => reloader.ReloadAsync(CancellationToken.None));

            // The first capability is live end-to-end.
            var before = await OrchestratorTool.ListCapabilities(registry, NullLogger<OrchestratorTool>.Instance);
            Assert.Contains("jira", before);
            Assert.DoesNotContain("codegen", before);
            Assert.NotEmpty(await connections.ListToolsAsync("jira", CancellationToken.None));

            // Append a second capability to the file at runtime.
            await File.WriteAllTextAsync(configPath, DemoConfig("jira", "codegen"));

            await Wait.ForAssertionAsync(() => Assert.Contains("codegen", registry.Names));
            var after = await OrchestratorTool.ListCapabilities(registry, NullLogger<OrchestratorTool>.Instance);
            Assert.Contains("jira", after);
            Assert.Contains("codegen", after);

            // And the new capability is immediately routable — connects lazily like at startup.
            Assert.NotEmpty(await connections.ListToolsAsync("codegen", CancellationToken.None));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>A config file launching the built demo server once per requested persona.</summary>
    private static string DemoConfig(params string[] personas)
    {
        var demo = Demo.DemoDll.Replace('\\', '/');
        var entries = personas.Select(p => $$"""
                { "name": "{{p}}", "summary": "Demo ({{p}}).", "command": "dotnet", "args": ["{{demo}}", "--persona", "{{p}}"] }
            """);
        return $$"""
            {
              "capabilities": [
            {{string.Join(",\n", entries)}}
              ]
            }
            """;
    }
}
