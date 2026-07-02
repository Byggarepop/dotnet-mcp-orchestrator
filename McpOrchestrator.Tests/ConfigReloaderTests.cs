using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Reload;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers the diff + apply core of config hot reload — which capability edits retire the
/// downstream connection and which don't — plus the last-known-good behavior when a reload is
/// rejected. Connection lifecycle is asserted through a spy; no real processes are spawned.
/// </summary>
public sealed class ConfigReloaderTests
{
    // ----- diff + apply against a spy connection lifecycle --------------------------------------

    [Fact]
    public async Task Added_capability_registers_without_touching_connections()
    {
        var (reloader, registry, spy) = Harness(Cap("jira"));

        var diff = await reloader.ApplyAsync(Next(Cap("jira"), Cap("codegen")), CancellationToken.None);

        Assert.Equal(new[] { "codegen" }, diff.Added);
        Assert.Equal(1, diff.Unchanged);
        Assert.NotNull(registry.Find("codegen"));
        Assert.Empty(spy.Invalidated);
    }

    [Fact]
    public async Task Removed_capability_is_invalidated_and_unregistered()
    {
        var (reloader, registry, spy) = Harness(Cap("jira"), Cap("codegen"));

        var diff = await reloader.ApplyAsync(Next(Cap("jira")), CancellationToken.None);

        Assert.Equal(new[] { "codegen" }, diff.Removed);
        Assert.Null(registry.Find("codegen"));
        Assert.NotNull(registry.Find("jira"));
        Assert.Equal(new[] { "codegen" }, spy.Invalidated);
    }

    [Fact]
    public async Task Command_change_retires_the_old_connection()
    {
        var (reloader, registry, spy) = Harness(Cap("jira", command: "old-cmd"));

        var diff = await reloader.ApplyAsync(Next(Cap("jira", command: "new-cmd")), CancellationToken.None);

        Assert.Equal(new[] { "jira" }, diff.Restarted);
        Assert.Equal(new[] { "jira" }, spy.Invalidated);
        Assert.Equal("new-cmd", registry.Find("jira")!.Command);
    }

    [Fact]
    public async Task Summary_only_change_updates_metadata_without_restarting()
    {
        var (reloader, registry, spy) = Harness(Cap("jira", summary: "before"));

        var diff = await reloader.ApplyAsync(Next(Cap("jira", summary: "after")), CancellationToken.None);

        Assert.Equal(new[] { "jira" }, diff.MetadataUpdated);
        Assert.Empty(spy.Invalidated);
        Assert.Equal("after", registry.Find("jira")!.Summary);
    }

    [Fact]
    public async Task Env_with_same_values_in_different_order_is_not_a_restart()
    {
        // Guards against diffing env by reference or entry order: a re-serialized config with the
        // same variables must never restart the downstream.
        var (reloader, _, spy) = Harness(
            Cap("jira", env: new() { ["A"] = "1", ["B"] = "2" }));

        var diff = await reloader.ApplyAsync(
            Next(Cap("jira", env: new() { ["B"] = "2", ["A"] = "1" })), CancellationToken.None);

        Assert.Empty(spy.Invalidated);
        Assert.Equal(1, diff.Unchanged);
    }

    [Fact]
    public async Task Env_value_change_is_a_restart()
    {
        var (reloader, _, spy) = Harness(Cap("jira", env: new() { ["A"] = "1" }));

        var diff = await reloader.ApplyAsync(
            Next(Cap("jira", env: new() { ["A"] = "2" })), CancellationToken.None);

        Assert.Equal(new[] { "jira" }, diff.Restarted);
        Assert.Equal(new[] { "jira" }, spy.Invalidated);
    }

    [Fact]
    public async Task Disabling_a_capability_hides_it_but_keeps_the_downstream()
    {
        var (reloader, registry, spy) = Harness(Cap("jira"));

        var diff = await reloader.ApplyAsync(Next(Cap("jira", enabled: false)), CancellationToken.None);

        // Hidden from routing (same as unknown), but the live connection is not torn down —
        // re-enabling must not have restarted the downstream.
        Assert.Null(registry.Find("jira"));
        Assert.Empty(spy.Invalidated);
        Assert.Equal(new[] { "jira" }, diff.MetadataUpdated);

        var reEnabled = await reloader.ApplyAsync(Next(Cap("jira")), CancellationToken.None);
        Assert.Empty(spy.Invalidated);
        Assert.NotNull(registry.Find("jira"));
        Assert.Equal(new[] { "jira" }, reEnabled.MetadataUpdated);
    }

    [Theory]
    [InlineData("workingDirectory")]
    [InlineData("args")]
    [InlineData("transport")]
    [InlineData("connectTimeout")]
    [InlineData("callTimeout")]
    public void Launch_relevant_field_changes_require_a_restart(string field)
    {
        var previous = Cap("jira");
        var next = Cap("jira");
        switch (field)
        {
            case "workingDirectory": next.WorkingDirectory = "/elsewhere"; break;
            case "args": next.Args = new() { "--other" }; break;
            case "transport": next.Transport = "future-transport"; break;
            case "connectTimeout": next.ConnectTimeoutSeconds = 5; break;
            case "callTimeout": next.CallTimeoutSeconds = 5; break;
        }

        Assert.True(ConfigReloader.RequiresRestart(previous, next));
        Assert.False(ConfigReloader.RequiresRestart(previous, Cap("jira")));
    }

    // ----- load + validate: last-known-good --------------------------------------------------

    [Fact]
    public async Task Invalid_json_rejects_the_reload_and_keeps_the_running_config()
    {
        await InTempConfigAsync(async (configPath, registry, spy, log) =>
        {
            var reloader = new ConfigReloader(new FileConfigSource(configPath, log), registry, spy, log);
            await File.WriteAllTextAsync(configPath, "{ this is not json !!");

            var diff = await reloader.ReloadAsync(CancellationToken.None);

            Assert.Null(diff);
            Assert.NotNull(registry.Find("keep"));
            Assert.Empty(spy.Invalidated);
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("reload rejected"));
        });
    }

    [Fact]
    public async Task Duplicate_capability_names_reject_the_reload()
    {
        await InTempConfigAsync(async (configPath, registry, spy, log) =>
        {
            var reloader = new ConfigReloader(new FileConfigSource(configPath, log), registry, spy, log);
            await File.WriteAllTextAsync(configPath, """
                {
                  "capabilities": [
                    { "name": "dup", "summary": "a", "command": "cmd-a" },
                    { "name": "dup", "summary": "b", "command": "cmd-b" }
                  ]
                }
                """);

            var diff = await reloader.ReloadAsync(CancellationToken.None);

            Assert.Null(diff);
            Assert.NotNull(registry.Find("keep"));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("duplicate"));
        });
    }

    [Fact]
    public async Task Enabled_entry_without_a_command_rejects_the_reload()
    {
        await InTempConfigAsync(async (configPath, registry, spy, log) =>
        {
            var reloader = new ConfigReloader(new FileConfigSource(configPath, log), registry, spy, log);
            await File.WriteAllTextAsync(configPath, """
                { "capabilities": [ { "name": "broken", "summary": "no command" } ] }
                """);

            var diff = await reloader.ReloadAsync(CancellationToken.None);

            Assert.Null(diff);
            Assert.NotNull(registry.Find("keep"));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("launch command"));
        });
    }

    // ----- helpers -------------------------------------------------------------------------------

    private static CapabilityDescriptor Cap(
        string name, string command = "some-cmd", string summary = "s",
        Dictionary<string, string?>? env = null, bool enabled = true) => new()
    {
        Name = name,
        Summary = summary,
        Enabled = enabled,
        Transport = "stdio",
        Command = command,
        Args = new() { "--flag" },
        Env = env ?? new(),
    };

    private static (ConfigReloader Reloader, CapabilityRegistry Registry, SpyLifecycle Spy) Harness(
        params CapabilityDescriptor[] initial)
    {
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors(initial, NullLogger.Instance));
        var spy = new SpyLifecycle();
        var reloader = new ConfigReloader(
            new FileConfigSource("unused.json", NullLogger.Instance), registry, spy, NullLogger.Instance);
        return (reloader, registry, spy);
    }

    private static ReloadedConfig Next(params CapabilityDescriptor[] capabilities) =>
        new(CapabilityCatalog.FromDescriptors(capabilities, NullLogger.Instance), capabilities);

    /// <summary>Runs a test body against a temp config file whose running config holds one capability "keep".</summary>
    private static async Task InTempConfigAsync(
        Func<string, CapabilityRegistry, SpyLifecycle, CollectingLogger, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"reload-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, "orchestrator.config.json");
            var registry = new CapabilityRegistry(
                CapabilityCatalog.FromDescriptors(new[] { Cap("keep") }, NullLogger.Instance));
            await body(configPath, registry, new SpyLifecycle(), new CollectingLogger());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

}
