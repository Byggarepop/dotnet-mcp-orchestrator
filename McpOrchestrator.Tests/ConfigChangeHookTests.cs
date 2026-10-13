using System.Runtime.InteropServices;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Reload;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers the config change hook: how the script is configured, how the launch is shaped per
/// script type, that the reload pipeline runs it after an applied change (and not for the
/// startup load or a rejected reload), and one real run of a script that reads the diff.
/// </summary>
public sealed class ConfigChangeHookTests
{
    // ----- configuration -------------------------------------------------------------------------

    [Theory]
    [InlineData(new[] { "--on-config-change", "C:/p/hook.ps1" }, "C:/p/hook.ps1")]
    [InlineData(new[] { "--on-config-change=C:/p/hook.py" }, "C:/p/hook.py")]
    [InlineData(new[] { "--trace-out", "x.jsonl", "--on-config-change", "hook.cmd" }, "hook.cmd")]
    [InlineData(new[] { "--on-config-change" }, null)]
    public void Script_path_comes_from_the_command_line(string[] args, string? expected)
    {
        Assert.Equal(expected, ScriptConfigChangeHook.ResolveScriptPath(args));
    }

    [Fact]
    public void Script_path_falls_back_to_the_environment_variable()
    {
        Environment.SetEnvironmentVariable(ScriptConfigChangeHook.EnvironmentVariable, "from-env.ps1");
        try
        {
            Assert.Equal("from-env.ps1", ScriptConfigChangeHook.ResolveScriptPath(Array.Empty<string>()));
            Assert.Equal("from-args.ps1", ScriptConfigChangeHook.ResolveScriptPath(new[] { "--on-config-change", "from-args.ps1" }));
        }
        finally
        {
            Environment.SetEnvironmentVariable(ScriptConfigChangeHook.EnvironmentVariable, null);
        }
    }

    [Fact]
    public void No_script_configured_means_no_hook()
    {
        Assert.Null(ScriptConfigChangeHook.ResolveScriptPath(Array.Empty<string>()));
        Assert.Null(ScriptConfigChangeHook.FromArguments(Array.Empty<string>(), NullLogger.Instance));
    }

    // ----- launch shape ----------------------------------------------------------------------------

    [Theory]
    [InlineData("hook.ps1", "pwsh", "-File")]
    [InlineData("hook.PS1", "pwsh", "-File")]
    [InlineData("hook.cmd", "cmd", "/c")]
    [InlineData("hook.bat", "cmd", "/c")]
    [InlineData("hook.sh", "bash", null)]
    public void Interpreter_is_picked_by_extension(string file, string interpreter, string? marker)
    {
        var script = Path.Combine(Path.GetTempPath(), "orch-hook", file);
        var info = ScriptConfigChangeHook.BuildStartInfo(script, Payload(), "{}");

        Assert.Equal(interpreter, info.FileName);
        Assert.Contains(script, info.ArgumentList);
        if (marker is not null)
        {
            Assert.Contains(marker, info.ArgumentList);
        }
        Assert.Equal(Path.GetDirectoryName(script), info.WorkingDirectory);
        Assert.True(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError);
    }

    [Fact]
    public void Python_scripts_use_the_platform_interpreter_name()
    {
        var info = ScriptConfigChangeHook.BuildStartInfo("/x/hook.py", Payload(), "{}");
        Assert.Equal(RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "python" : "python3", info.FileName);
    }

    [Fact]
    public void Unknown_extensions_run_the_file_directly()
    {
        var info = ScriptConfigChangeHook.BuildStartInfo("/x/hook.exe", Payload(), "{}");
        Assert.Equal("/x/hook.exe", info.FileName);
        Assert.Empty(info.ArgumentList);
    }

    [Fact]
    public void Diff_is_exposed_as_environment_variables_and_json()
    {
        var payload = Payload(added: new[] { "jira", "codegen" }, removed: new[] { "old" }, restarted: new[] { "db" }, updated: new[] { "docs" });
        var json = System.Text.Json.JsonSerializer.Serialize(payload, ConfigChangeJsonContext.Default.ConfigChangePayload);
        var info = ScriptConfigChangeHook.BuildStartInfo("/x/hook.ps1", payload, json);

        Assert.Equal("https://example/orchestrator.json", info.Environment["MCP_ORCHESTRATOR_CHANGE_SOURCE"]);
        Assert.Equal("config-change", info.Environment["MCP_ORCHESTRATOR_CHANGE_TRIGGER"]);
        Assert.Equal("", info.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_ADDED"]);
        Assert.Equal("jira;codegen", info.Environment["MCP_ORCHESTRATOR_CHANGE_ADDED"]);
        Assert.Equal("old", info.Environment["MCP_ORCHESTRATOR_CHANGE_REMOVED"]);
        Assert.Equal("db", info.Environment["MCP_ORCHESTRATOR_CHANGE_RESTARTED"]);
        Assert.Equal("docs", info.Environment["MCP_ORCHESTRATOR_CHANGE_UPDATED"]);
        Assert.Equal(json, info.Environment["MCP_ORCHESTRATOR_CHANGE_JSON"]);
        Assert.Contains("\"added\":[\"jira\",\"codegen\"]", json);
        Assert.Contains("\"source\":\"https://example/orchestrator.json\"", json);
        Assert.Contains("\"unchanged\":3", json);
    }

    // ----- pipeline integration ----------------------------------------------------------------

    [Fact]
    public async Task Hook_runs_after_an_applied_change_with_the_diff()
    {
        var hook = new RecordingHook();
        var (reloader, _) = Harness(hook, Next(Cap("jira"), Cap("codegen")), Cap("jira"));

        var diff = await reloader.ReloadAsync(CancellationToken.None);

        Assert.NotNull(diff);
        var change = Assert.Single(hook.Changes);
        Assert.Equal("stub-source", change.Source);
        Assert.Equal("config-change", change.Trigger);
        Assert.Equal(new[] { "codegen" }, change.Diff.Added);
        Assert.Equal(1, change.Diff.Unchanged);
    }

    [Fact]
    public async Task Skill_diff_from_the_skills_sink_rides_on_the_change()
    {
        var hook = new RecordingHook();
        var sink = new StubSkillsSink(new McpOrchestrator.Orchestration.Skills.SkillDiff(["publish-release"], [], ["release-notes"], ["orchestrator-repo"]));
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors([Cap("jira")], NullLogger.Instance));
        var reloader = new ConfigReloader(
            new StubSource(Next(Cap("jira"))), registry, new SpyLifecycle(), NullLogger.Instance, sink, hook);

        await reloader.ReloadAsync(CancellationToken.None);

        var change = Assert.Single(hook.Changes);
        var payload = ScriptConfigChangeHook.ToPayload(change);
        Assert.Equal(new[] { "publish-release" }, payload.SkillsAdded);
        Assert.Equal(new[] { "release-notes" }, payload.SkillsUpdated);
        Assert.Empty(payload.SkillsRemoved);
        var json = System.Text.Json.JsonSerializer.Serialize(payload, ConfigChangeJsonContext.Default.ConfigChangePayload);
        Assert.Contains("\"skillsAdded\":[\"publish-release\"]", json);
        var info = ScriptConfigChangeHook.BuildStartInfo("/x/hook.ps1", payload, json);
        Assert.Equal("publish-release", info.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_ADDED"]);
        Assert.Equal("release-notes", info.Environment["MCP_ORCHESTRATOR_CHANGE_SKILLS_UPDATED"]);
    }

    [Fact]
    public async Task Hook_does_not_run_for_the_startup_load()
    {
        var hook = new RecordingHook();
        var (reloader, _) = Harness(hook, Next(Cap("jira")));

        await reloader.ReloadAsync(CancellationToken.None, ReloadReason.Startup);

        Assert.Empty(hook.Changes);
    }

    [Fact]
    public async Task Hook_does_not_run_when_the_reload_is_rejected()
    {
        var hook = new RecordingHook();
        var (reloader, _) = Harness(hook, next: null, Cap("jira"));

        var diff = await reloader.ReloadAsync(CancellationToken.None);

        Assert.Null(diff);
        Assert.Empty(hook.Changes);
    }

    [Fact]
    public async Task A_throwing_hook_never_fails_the_reload()
    {
        var (reloader, registry) = Harness(new ThrowingHook(), Next(Cap("jira"), Cap("codegen")), Cap("jira"));

        // ConfigReloader catches everything after the apply; the new catalog stays live.
        var diff = await reloader.ReloadAsync(CancellationToken.None);

        Assert.Null(diff); // reported as failed...
        Assert.NotNull(registry.Find("codegen")); // ...but the config was applied before the hook ran.
    }

    // ----- a real script -----------------------------------------------------------------------

    [Fact]
    public async Task A_real_script_receives_the_change()
    {
        var dir = Directory.CreateTempSubdirectory("orch-hook-");
        try
        {
            var output = Path.Combine(dir.FullName, "seen.txt");
            string script;
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                script = Path.Combine(dir.FullName, "hook.cmd");
                File.WriteAllText(script,
                    "@echo off\r\n"
                    + "echo added=%MCP_ORCHESTRATOR_CHANGE_ADDED% source=%MCP_ORCHESTRATOR_CHANGE_SOURCE% cwd=%CD%> seen.txt\r\n"
                    + "echo hook says hi\r\n");
            }
            else
            {
                script = Path.Combine(dir.FullName, "hook.sh");
                File.WriteAllText(script,
                    "#!/bin/sh\n"
                    + "echo \"added=$MCP_ORCHESTRATOR_CHANGE_ADDED source=$MCP_ORCHESTRATOR_CHANGE_SOURCE cwd=$PWD\" > seen.txt\n"
                    + "echo hook says hi\n");
            }

            var logger = new CollectingLogger();
            var hook = new ScriptConfigChangeHook(script, TimeSpan.FromSeconds(60), logger);
            var (reloader, _) = Harness(hook, Next(Cap("jira"), Cap("codegen")), Cap("jira"));

            await reloader.ReloadAsync(CancellationToken.None);

            var seen = File.ReadAllText(output).Trim();
            Assert.StartsWith("added=codegen source=stub-source cwd=", seen);
            Assert.EndsWith(Path.TrimEndingDirectorySeparator(dir.FullName), seen, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(logger.Entries, e => e.Message.Contains("[hook] hook says hi"));
            Assert.Contains(logger.Entries, e => e.Message.Contains("Config change hook finished"));
        }
        finally
        {
            dir.Delete(recursive: true);
        }
    }

    [Fact]
    public async Task A_missing_script_logs_an_error_and_keeps_the_reload()
    {
        var logger = new CollectingLogger();
        var hook = new ScriptConfigChangeHook(Path.Combine(Path.GetTempPath(), "orch-missing-hook.ps1"), TimeSpan.FromSeconds(5), logger);
        var (reloader, registry) = Harness(hook, Next(Cap("jira"), Cap("codegen")), Cap("jira"));

        var diff = await reloader.ReloadAsync(CancellationToken.None);

        Assert.NotNull(diff);
        Assert.NotNull(registry.Find("codegen"));
        Assert.Contains(logger.Entries, e => e.Message.Contains("script not found"));
    }

    // ----- helpers ---------------------------------------------------------------------------------

    private static ConfigChangePayload Payload(
        string[]? added = null, string[]? removed = null, string[]? restarted = null, string[]? updated = null) =>
        new("https://example/orchestrator.json", "config-change", DateTimeOffset.UnixEpoch,
            added ?? Array.Empty<string>(), removed ?? Array.Empty<string>(),
            restarted ?? Array.Empty<string>(), updated ?? Array.Empty<string>(), 3,
            Array.Empty<string>(), Array.Empty<string>(), Array.Empty<string>());

    private static CapabilityDescriptor Cap(string name) => new()
    {
        Name = name,
        Summary = name,
        Command = "cmd",
        Transport = "stdio",
        Enabled = true,
    };

    private static ReloadedConfig Next(params CapabilityDescriptor[] capabilities) =>
        new(CapabilityCatalog.FromDescriptors(capabilities, NullLogger.Instance), capabilities);

    private static (ConfigReloader Reloader, CapabilityRegistry Registry) Harness(
        IConfigChangeHook hook, ReloadedConfig? next, params CapabilityDescriptor[] initial)
    {
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors(initial, NullLogger.Instance));
        var reloader = new ConfigReloader(new StubSource(next), registry, new SpyLifecycle(), NullLogger.Instance, hook: hook);
        return (reloader, registry);
    }

    private sealed class StubSource : IConfigSource
    {
        private readonly ReloadedConfig? _next;
        public StubSource(ReloadedConfig? next) => _next = next;
        public string Description => "stub-source";
        public Task<ReloadedConfig?> TryLoadAsync(CancellationToken cancellationToken) => Task.FromResult(_next);
    }

    private sealed class StubSkillsSink : McpOrchestrator.Orchestration.Skills.ISkillsReloadSink
    {
        private readonly McpOrchestrator.Orchestration.Skills.SkillDiff _diff;
        public StubSkillsSink(McpOrchestrator.Orchestration.Skills.SkillDiff diff) => _diff = diff;
        public Task<McpOrchestrator.Orchestration.Skills.SkillDiff> ApplyAsync(McpOrchestrator.Orchestration.Skills.SkillsOptions? options, CancellationToken cancellationToken) => Task.FromResult(_diff);
    }

    private sealed class RecordingHook : IConfigChangeHook
    {
        public List<ConfigChangeEvent> Changes { get; } = new();

        public Task RunAsync(ConfigChangeEvent change, CancellationToken cancellationToken)
        {
            Changes.Add(change);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingHook : IConfigChangeHook
    {
        public Task RunAsync(ConfigChangeEvent change, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
