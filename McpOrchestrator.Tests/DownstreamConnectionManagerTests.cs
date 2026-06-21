using McpOrchestrator.Orchestration;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Integration tests that launch the demo server as a real downstream MCP process and drive
/// it through <see cref="DownstreamConnectionManager"/>. These exercise the live stdio
/// transport, connection caching, error surfacing, and timeouts.
/// </summary>
[Trait("Category", "Integration")]
public sealed class DownstreamConnectionManagerTests
{
    [Fact]
    public async Task Lists_tools_of_a_capability()
    {
        await using var conn = Demo.Connections(Demo.Capability("jira", "jira"));

        var tools = await conn.ListToolsAsync("jira", CancellationToken.None);

        Assert.Contains(tools, t => t.Name == "get_issue");
        Assert.Contains(tools, t => t.Name == "search_issues");
    }

    [Fact]
    public async Task Calls_a_tool_and_returns_its_result()
    {
        await using var conn = Demo.Connections(Demo.Capability("jira", "jira"));

        var result = await conn.CallToolAsync(
            "jira", "get_issue",
            new Dictionary<string, object?> { ["issueKey"] = "PROJ-1" },
            CancellationToken.None);

        Assert.NotEqual(true, result.IsError);
        var text = ToolPayloads.FlattenText(result);
        Assert.Contains("PROJ-1", text);
        Assert.Contains("In Progress", text);
    }

    [Fact]
    public async Task Unknown_capability_throws_CapabilityNotFound()
    {
        await using var conn = Demo.Connections(Demo.Capability("jira", "jira"));

        var ex = await Assert.ThrowsAsync<CapabilityNotFoundException>(
            () => conn.CallToolAsync("nope", "get_issue", new Dictionary<string, object?>(), CancellationToken.None));

        Assert.Contains("jira", ex.Available);
    }

    [Fact]
    public async Task Downstream_tool_failure_comes_back_as_IsError()
    {
        await using var conn = Demo.Connections(Demo.Capability("diag", "diag"));

        var result = await conn.CallToolAsync(
            "diag", "fail",
            new Dictionary<string, object?> { ["reason"] = "boom" },
            CancellationToken.None);

        Assert.True(result.IsError);
    }

    [Fact]
    public async Task Call_that_exceeds_its_timeout_throws_TimeoutException()
    {
        await using var conn = Demo.Connections(Demo.Capability("diag", "diag", callTimeout: 1));

        await Assert.ThrowsAsync<TimeoutException>(
            () => conn.CallToolAsync(
                "diag", "slow",
                new Dictionary<string, object?> { ["delayMs"] = 8000 },
                CancellationToken.None));
    }

    [Fact]
    public async Task Connect_that_never_handshakes_times_out()
    {
        // A process that starts but never speaks MCP — the connect deadline must fire. Use an
        // OS-appropriate "sleep forever" command so this runs on Windows, Linux, and macOS.
        var (command, sleepArgs) = OperatingSystem.IsWindows()
            ? ("powershell", new List<string> { "-NoProfile", "-Command", "Start-Sleep -Seconds 30" })
            : ("sleep", new List<string> { "30" });
        var hang = new CapabilityDescriptor
        {
            Name = "hang",
            Command = command,
            Args = sleepArgs,
            ConnectTimeoutSeconds = 2,
        };
        await using var conn = Demo.Connections(hang);

        await Assert.ThrowsAsync<TimeoutException>(
            () => conn.ListToolsAsync("hang", CancellationToken.None));
    }

    [Fact]
    public async Task Bad_command_throws_and_a_retry_also_throws()
    {
        var bad = new CapabilityDescriptor
        {
            Name = "bad",
            Command = "dotnet",
            Args = new List<string> { Path.Combine(Demo.SolutionDir, "this-dll-does-not-exist.dll") },
            ConnectTimeoutSeconds = 15,
        };
        await using var conn = Demo.Connections(bad);

        // First attempt fails; the faulted connection is evicted so a second attempt retries
        // (and fails again) rather than hanging on a cached, never-completing task.
        await Assert.ThrowsAnyAsync<Exception>(
            () => conn.ListToolsAsync("bad", CancellationToken.None));
        await Assert.ThrowsAnyAsync<Exception>(
            () => conn.ListToolsAsync("bad", CancellationToken.None));
    }

    [Fact]
    public async Task Concurrent_calls_share_one_connection_and_all_succeed()
    {
        await using var conn = Demo.Connections(Demo.Capability("jira", "jira"));

        var calls = Enumerable.Range(0, 20).Select(_ =>
            conn.CallToolAsync(
                "jira", "get_issue",
                new Dictionary<string, object?> { ["issueKey"] = "PROJ-1" },
                CancellationToken.None));

        var results = await Task.WhenAll(calls);

        Assert.All(results, r =>
        {
            Assert.NotEqual(true, r.IsError);
            Assert.Contains("PROJ-1", ToolPayloads.FlattenText(r));
        });
    }
}
