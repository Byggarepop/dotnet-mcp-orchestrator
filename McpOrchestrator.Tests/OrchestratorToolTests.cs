using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// End-to-end tests of the four agent-facing tool methods on <see cref="OrchestratorTool"/>,
/// driven against the real demo downstream. These cover the full path: catalog → connection
/// manager → downstream MCP → structured JSON returned to the agent, including error paths.
/// </summary>
[Trait("Category", "Integration")]
public sealed class OrchestratorToolTests
{
    private static readonly ILogger<OrchestratorTool> Log = new NullLogger<OrchestratorTool>();

    private static JsonElement Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static JsonElement Obj(string json) => Parse(json);

    [Fact]
    public async Task ListCapabilities_advertises_configured_capabilities()
    {
        var (catalog, _) = Demo.StandardPair();

        var root = Parse(await OrchestratorTool.ListCapabilities(catalog, Log));

        var names = root.EnumerateArray().Select(c => c.GetProperty("name").GetString()).ToList();
        Assert.Contains("jira", names);
        Assert.Contains("codegen", names);
        Assert.Contains("diag", names);
    }

    [Fact]
    public async Task DiscoverTools_lists_downstream_tools()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var root = Parse(await OrchestratorTool.DiscoverTools(conn, catalog, Log, "jira", CancellationToken.None));

        var toolNames = root.GetProperty("tools").EnumerateArray()
            .Select(t => t.GetProperty("name").GetString()).ToList();
        Assert.Contains("get_issue", toolNames);
    }

    [Fact]
    public async Task DiscoverTools_unknown_capability_returns_structured_error()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var root = Parse(await OrchestratorTool.DiscoverTools(conn, catalog, Log, "does-not-exist", CancellationToken.None));

        Assert.True(root.TryGetProperty("error", out _));
        var available = root.GetProperty("availableCapabilities").EnumerateArray()
            .Select(a => a.GetString()).ToList();
        Assert.Contains("jira", available);
    }

    [Fact]
    public async Task Route_calls_the_tool_and_echoes_arguments()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var root = Parse(await OrchestratorTool.Route(
            conn, catalog, Log, "jira", "get_issue", Obj("""{ "issueKey": "PROJ-1" }"""), CancellationToken.None));

        Assert.False(root.GetProperty("isError").GetBoolean());
        Assert.Contains("In Progress", root.GetProperty("text").GetString());
        Assert.Equal("PROJ-1", root.GetProperty("arguments").GetProperty("issueKey").GetString());
    }

    [Fact]
    public async Task Route_accepts_arguments_passed_as_a_json_string()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var stringified = JsonSerializer.SerializeToElement("{ \"issueKey\": \"PROJ-2\" }");

        var root = Parse(await OrchestratorTool.Route(
            conn, catalog, Log, "jira", "get_issue", stringified, CancellationToken.None));

        Assert.False(root.GetProperty("isError").GetBoolean());
        Assert.Contains("PROJ-2", root.GetProperty("text").GetString());
    }

    [Fact]
    public async Task Route_to_failing_tool_reports_isError()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var root = Parse(await OrchestratorTool.Route(
            conn, catalog, Log, "diag", "fail", Obj("""{ "reason": "boom" }"""), CancellationToken.None));

        // Either the downstream returns an error result (isError) or the call faults into an
        // ErrorView — both are acceptable; what matters is it is surfaced, not swallowed.
        var surfaced = (root.TryGetProperty("isError", out var e) && e.GetBoolean())
            || root.TryGetProperty("error", out _);
        Assert.True(surfaced);
    }

    [Fact]
    public async Task Route_unknown_tool_is_surfaced_not_thrown()
    {
        var (catalog, conn) = Demo.StandardPair();
        await using var owned = conn;

        var root = Parse(await OrchestratorTool.Route(
            conn, catalog, Log, "jira", "no_such_tool", Obj("{}"), CancellationToken.None));

        var surfaced = (root.TryGetProperty("isError", out var e) && e.GetBoolean())
            || root.TryGetProperty("error", out _);
        Assert.True(surfaced);
    }
}
