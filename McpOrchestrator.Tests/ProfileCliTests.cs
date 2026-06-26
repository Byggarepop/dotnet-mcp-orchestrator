using McpOrchestrator;
using McpOrchestrator.Profiling;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Exercises the <c>profile</c> subcommand's argument handling and exit codes end-to-end through
/// <see cref="ProfileCommand.RunAsync"/>, capturing stdout/stderr. These paths all fail fast
/// (usage/validation), so none launch a downstream server.
/// </summary>
[Collection("ConsoleSerial")]
public sealed class ProfileCliTests
{
    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var code = await ProfileCommand.RunAsync(args);
            return (code, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    /// <summary>Runs <paramref name="body"/> with the process current directory set to a fresh empty
    /// temp dir, restoring it afterward. Safe because the ConsoleSerial collection serializes these.</summary>
    private static Task<T> InEmptyDir<T>(Func<Task<T>> body) => InTempDir(_ => body());

    private static async Task<T> InTempDir<T>(Func<string, Task<T>> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"profile-cli-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var prev = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(dir);
        try
        {
            return await body(dir);
        }
        finally
        {
            Directory.SetCurrentDirectory(prev);
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void DiscoverDefaultSource_prefers_orchestrator_config_then_host_configs()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"profile-discover-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Only a host config present → discovered as a host config.
            File.WriteAllText(Path.Combine(dir, ".mcp.json"), "{}");
            var hostHit = ProfileCommand.DiscoverDefaultSource(dir);
            Assert.NotNull(hostHit);
            Assert.True(hostHit!.Value.IsHostConfig);
            Assert.EndsWith(".mcp.json", hostHit.Value.Path);

            // Add the orchestrator's own config → it wins, as a non-host config.
            File.WriteAllText(Path.Combine(dir, "orchestrator.config.json"), "{}");
            var orchHit = ProfileCommand.DiscoverDefaultSource(dir);
            Assert.NotNull(orchHit);
            Assert.False(orchHit!.Value.IsHostConfig);
            Assert.EndsWith("orchestrator.config.json", orchHit.Value.Path);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DiscoverDefaultSource_returns_null_when_no_config_present()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"profile-discover-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            Assert.Null(ProfileCommand.DiscoverDefaultSource(dir));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Help_prints_usage_and_succeeds()
    {
        var (code, stdout, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("USAGE", stdout);
        Assert.Contains("--assert-favorable", stdout);
    }

    [Fact]
    public async Task No_arguments_with_nothing_to_discover_is_a_usage_error()
    {
        var (code, _, stderr) = await InEmptyDir(() => Run());
        Assert.Equal(1, code);
        Assert.Contains("config is required", stderr);
    }

    [Fact]
    public async Task Trace_without_config_and_nothing_to_discover_is_an_error()
    {
        var (code, _, stderr) = await InEmptyDir(() => Run("--trace", "session.jsonl"));
        Assert.Equal(1, code);
        Assert.Contains("--config", stderr);
    }

    [Fact]
    public async Task No_arguments_auto_detects_orchestrator_config_in_cwd()
    {
        var (code, _, stderr) = await InTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, "orchestrator.config.json"), "{ not valid json");
            return Run();
        });

        // Discovery picked the file up (announced on stderr) and handed it to the profiler, which
        // then failed on the bogus content — proving auto-detect wired the discovered path through.
        Assert.Contains("using discovered orchestrator config", stderr);
        Assert.Equal(1, code);
    }

    [Fact]
    public async Task No_arguments_auto_detects_host_config_in_cwd()
    {
        var (_, _, stderr) = await InTempDir(dir =>
        {
            File.WriteAllText(Path.Combine(dir, ".mcp.json"), "{ not valid json");
            return Run();
        });

        Assert.Contains("using discovered host config", stderr);
        Assert.Contains(".mcp.json", stderr);
    }

    [Fact]
    public async Task Assert_favorable_in_static_mode_is_an_error()
    {
        var (code, _, stderr) = await Run("--config", "whatever.json", "--assert-favorable");
        Assert.Equal(1, code);
        Assert.Contains("trace mode", stderr);
    }

    [Fact]
    public async Task Unknown_flag_is_an_error()
    {
        var (code, _, stderr) = await Run("--bogus");
        Assert.Equal(1, code);
        Assert.Contains("unknown option", stderr);
    }

    [Fact]
    public async Task Invalid_format_is_an_error()
    {
        var (code, _, stderr) = await Run("--config", "x.json", "--format", "xml");
        Assert.Equal(1, code);
        Assert.Contains("format", stderr);
    }

    [Fact]
    public async Task Missing_config_file_fails_loudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-config-{Guid.NewGuid():N}.json");
        var (code, _, stderr) = await Run("--config", missing);
        Assert.Equal(1, code);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public async Task Config_and_host_config_together_is_an_error()
    {
        var (code, _, stderr) = await Run("--config", "a.json", "--host-config", "b.json");
        Assert.Equal(1, code);
        Assert.Contains("mutually exclusive", stderr);
    }

    [Fact]
    public async Task Missing_host_config_file_fails_loudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-such-host-{Guid.NewGuid():N}.json");
        var (code, _, stderr) = await Run("--host-config", missing);
        Assert.Equal(1, code);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public async Task Help_mentions_the_host_config_try_path()
    {
        var (code, stdout, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("--host-config", stdout);
        Assert.Contains("dotnet tool execute", stdout);
    }

    [Theory]
    [InlineData("a.jsonl")]                 // --trace-out a.jsonl
    [InlineData("=b.jsonl")]                // --trace-out=b.jsonl
    public void TraceOut_path_is_resolved_from_args(string form)
    {
        var args = form.StartsWith('=')
            ? new[] { "--trace-out" + form }
            : new[] { "--trace-out", form };

        Assert.Equal(form.TrimStart('='), OrchestratorHost.ResolveTraceOutPath(args));
    }

    [Fact]
    public void TraceOut_path_falls_back_to_env_then_off()
    {
        var prev = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_TRACE_OUT");
        try
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_TRACE_OUT", null);
            Assert.Null(OrchestratorHost.ResolveTraceOutPath(Array.Empty<string>()));

            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_TRACE_OUT", "from-env.jsonl");
            Assert.Equal("from-env.jsonl", OrchestratorHost.ResolveTraceOutPath(Array.Empty<string>()));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_TRACE_OUT", prev);
        }
    }
}
