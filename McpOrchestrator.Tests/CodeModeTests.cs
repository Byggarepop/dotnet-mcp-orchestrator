using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.CodeMode;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Code mode's <c>execute</c>: the sandbox, the route bridge, the limits, and one end-to-end run
/// against the demo downstream.
/// </summary>
public sealed class CodeModeTests
{
    private static readonly CodeModeLimits Fast = CodeModeLimits.Default with { Timeout = TimeSpan.FromSeconds(3) };

    /// <summary>A fake router: answers like the orchestrator's route tool and records each call.</summary>
    private sealed class FakeRoutes
    {
        public List<(string Capability, string Tool, string Args)> Calls { get; } = new();

        public Task<string> Invoke(string capability, string tool, JsonElement args, CancellationToken ct)
        {
            Calls.Add((capability, tool, args.GetRawText()));
            return Task.FromResult(tool switch
            {
                "rows" => OrchestratorJson.Serialize(new RouteView
                {
                    Capability = capability, Tool = tool,
                    Text = JsonSerializer.Serialize(Enumerable.Range(1, 150)
                        .Select(i => new Dictionary<string, object> { ["id"] = i, ["status"] = i % 50 == 0 ? "ERR" : "OK" })),
                }),
                "wrapped" => """{"capability":"c","tool":"wrapped","isError":false,"structured":{"result":[1,2,3]}}""",
                "plain" => """{"capability":"c","tool":"plain","isError":false,"text":"public sealed class Customer { }"}""",
                "fail" => """{"capability":"c","tool":"fail","isError":true,"text":"Downstream capability 'c' tool 'fail' failed: boom"}""",
                _ => """{"error":"Unknown capability","availableCapabilities":["c"]}""",
            });
        }
    }

    private static async Task<JsonElement> Run(string code, FakeRoutes routes, CodeModeLimits? limits = null)
    {
        var json = await CodeModeRunner.RunAsync(code, routes.Invoke, limits ?? Fast, CancellationToken.None);
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task Returns_the_scripts_value_and_counts_route_calls()
    {
        var routes = new FakeRoutes();

        var root = await Run("""
            const rows = await route('c', 'rows', {});
            return rows.filter(r => r.status === 'ERR').map(r => r.id);
            """, routes);

        Assert.Equal(new[] { 50, 100, 150 }, root.GetProperty("result").EnumerateArray().Select(e => e.GetInt32()));
        Assert.Equal(1, root.GetProperty("routeCalls").GetInt32());
        Assert.False(root.TryGetProperty("error", out _));
    }

    [Fact]
    public async Task Passes_arguments_through_and_supports_Promise_all()
    {
        var routes = new FakeRoutes();

        var root = await Run("""
            const ids = ['A', 'B', 'C'];
            const all = await Promise.all(ids.map(id => route('c', 'wrapped', { id })));
            return all.length;
            """, routes);

        Assert.Equal(3, root.GetProperty("result").GetInt32());
        Assert.Equal(["{\"id\":\"A\"}", "{\"id\":\"B\"}", "{\"id\":\"C\"}"], routes.Calls.Select(c => c.Args));
    }

    [Fact]
    public async Task Unwraps_sdk_result_wrapper_and_keeps_plain_text_as_string()
    {
        var root = await Run("""
            return { wrapped: await route('c', 'wrapped'), plain: await route('c', 'plain') };
            """, new FakeRoutes());

        Assert.Equal(3, root.GetProperty("result").GetProperty("wrapped").GetArrayLength());
        Assert.Equal("public sealed class Customer { }", root.GetProperty("result").GetProperty("plain").GetString());
    }

    [Fact]
    public async Task A_failed_route_throws_and_can_be_caught()
    {
        var root = await Run("""
            try { await route('c', 'fail', {}); return 'not reached'; }
            catch (e) { return { message: e.message, isError: e.details.isError }; }
            """, new FakeRoutes());

        Assert.Contains("boom", root.GetProperty("result").GetProperty("message").GetString());
        Assert.True(root.GetProperty("result").GetProperty("isError").GetBoolean());
    }

    [Fact]
    public async Task An_uncaught_route_failure_becomes_an_error()
    {
        var root = await Run("return await route('nope', 'x', {});", new FakeRoutes());

        Assert.Contains("Unknown capability", root.GetProperty("error").GetString());
        Assert.False(root.TryGetProperty("result", out _));
    }

    [Fact]
    public async Task Route_call_limit_is_enforced()
    {
        var routes = new FakeRoutes();

        var root = await Run(
            "for (let i = 0; i < 10; i++) { await route('c', 'wrapped'); } return 'done';",
            routes,
            Fast with { MaxRouteCalls = 3 });

        Assert.Contains("route call limit (3)", root.GetProperty("error").GetString());
        Assert.Equal(3, routes.Calls.Count);
    }

    [Fact]
    public async Task Endless_loop_is_stopped()
    {
        var root = await Run("while (true) {}", new FakeRoutes(), Fast with { MaxStatements = 100_000 });

        Assert.Contains("limit", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Endless_loop_is_stopped_by_the_time_limit()
    {
        var root = await Run(
            "while (true) {}", new FakeRoutes(),
            Fast with { Timeout = TimeSpan.FromMilliseconds(500), MaxStatements = int.MaxValue });

        Assert.Contains("time limit", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Scripts_have_no_clr_or_host_access()
    {
        var root = await Run("""
            return [typeof System, typeof importNamespace, typeof require, typeof process, typeof fetch];
            """, new FakeRoutes());

        Assert.All(root.GetProperty("result").EnumerateArray(), e => Assert.Equal("undefined", e.GetString()));
    }

    [Fact]
    public async Task Syntax_errors_are_reported()
    {
        var root = await Run("return {;", new FakeRoutes());

        Assert.Contains("Unexpected token", root.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Undefined_return_is_null()
    {
        var root = await Run("const x = 1;", new FakeRoutes());

        Assert.Equal(JsonValueKind.Null, root.GetProperty("result").ValueKind);
    }

    [Theory]
    [InlineData("1", true)]
    [InlineData("true", true)]
    [InlineData(" TRUE ", true)]
    [InlineData("0", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Enabled_only_when_switched_on(string? value, bool expected)
    {
        Assert.Equal(expected, CodeModeTool.IsEnabled(name => name == CodeModeTool.EnvironmentVariable ? value : null));
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Execute_runs_a_chain_against_a_real_downstream()
    {
        var (catalog, conn) = Demo.Pair(Demo.Capability("jira", "jira"));
        await using var owned = conn;

        var json = await CodeModeTool.Execute(
            conn, catalog, new NullLogger<OrchestratorTool>(), new NullLogger<CodeModeTool>(),
            """
            const found = await route('jira', 'search_issues', { query: '' });
            const issues = await Promise.all(found.map(i => route('jira', 'get_issue', { issueKey: i.key })));
            return issues.map(i => i.key + ': ' + i.status);
            """,
            CancellationToken.None);

        using var doc = JsonDocument.Parse(json);
        var lines = doc.RootElement.GetProperty("result").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("PROJ-1: In Progress", lines);
        Assert.Equal(lines.Count + 1, doc.RootElement.GetProperty("routeCalls").GetInt32());
    }
}
