using System.Text.Json;
using McpOrchestrator.Orchestration.LocalLlm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace McpOrchestrator.Tests;

/// <summary>
/// Live end-to-end test of the embedded local LLM: it downloads the tiny GGUF model on first
/// run and routes a real request against the demo downstream. It is gated behind the
/// <c>RUN_LLM_LIVE=1</c> environment variable so normal test runs never pull a ~400 MB model;
/// when disabled it returns immediately. Run it with:
/// <c>RUN_LLM_LIVE=1 dotnet test --filter FullyQualifiedName~LiveLocalLlmTests</c>.
/// </summary>
[Trait("Category", "Live")]
public sealed class LiveLocalLlmTests
{
    private readonly ITestOutputHelper _output;

    public LiveLocalLlmTests(ITestOutputHelper output) => _output = output;

    private static bool Enabled =>
        string.Equals(Environment.GetEnvironmentVariable("RUN_LLM_LIVE"), "1", StringComparison.Ordinal);

    [Fact]
    public async Task Local_model_routes_a_jira_request_end_to_end()
    {
        if (!Enabled)
        {
            return; // disabled by default; see class summary.
        }

        await using var conn = Demo.Connections(Demo.Capability("jira", "jira"));
        var tools = await conn.ListToolsAsync("jira", CancellationToken.None);

        var options = new LocalLlmOptions { Enabled = true };
        var provisioner = new ModelProvisioner(options, NullLogger.Instance);
        await using var llm = new LocalLlm(options, provisioner, NullLogger.Instance);
        var planner = new LlmRoutePlanner(llm, new NullLogger<LlmRoutePlanner>(), options.ModelFileName);

        var started = DateTime.UtcNow;
        var plan = await planner.PlanAsync("jira", tools, "what is the current status of PROJ-1?", CancellationToken.None);
        _output.WriteLine($"Planned in {(DateTime.UtcNow - started).TotalSeconds:0.0}s: tool={plan?.Tool}, args={JsonSerializer.Serialize(plan?.Arguments)}");

        Assert.NotNull(plan);
        Assert.Equal("get_issue", plan!.Tool);
        Assert.True(plan.Arguments.ContainsKey("issueKey"), "expected the planner to fill issueKey");
        Assert.Equal("PROJ-1", ((JsonElement)plan.Arguments["issueKey"]!).GetString());
    }
}
