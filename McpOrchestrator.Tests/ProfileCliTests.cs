using McpOrchestrator;
using McpOrchestrator.Profiling;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Exercises the <c>profile</c> subcommand's argument handling and exit codes end-to-end through
/// <see cref="ProfileCommand.RunAsync"/>, capturing stdout/stderr. These paths all fail fast
/// (usage/validation), so none launch a downstream server.
/// </summary>
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

    [Fact]
    public async Task Help_prints_usage_and_succeeds()
    {
        var (code, stdout, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("USAGE", stdout);
        Assert.Contains("--assert-favorable", stdout);
    }

    [Fact]
    public async Task No_arguments_is_a_usage_error()
    {
        var (code, _, stderr) = await Run();
        Assert.Equal(1, code);
        Assert.Contains("mode is required", stderr);
    }

    [Fact]
    public async Task Trace_without_config_is_an_error()
    {
        var (code, _, stderr) = await Run("--trace", "session.jsonl");
        Assert.Equal(1, code);
        Assert.Contains("--config", stderr);
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
