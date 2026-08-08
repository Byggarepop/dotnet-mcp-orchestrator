using System.IO.Pipelines;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// End-to-end: a real MCP server host wired like <c>OrchestratorHost</c> (orchestrator tools +
/// the tools/call validation filter), talked to by a real <see cref="McpClient"/> over
/// in-process pipe streams, proxying to the real demo downstream. Covers the failure mode
/// that motivated the filter: a model calling <c>route</c> with the parameter name
/// <c>args</c> used to die in SDK argument binding with the generic
/// "An error occurred invoking 'route'".
/// </summary>
[Trait("Category", "Integration")]
public sealed class ToolCallValidationEndToEndTests : IAsyncLifetime
{
    private readonly Pipe _clientToServer = new();
    private readonly Pipe _serverToClient = new();
    private IHost? _host;
    private DownstreamConnectionManager? _connections;
    private McpClient? _client;

    public async Task InitializeAsync()
    {
        var (catalog, connections) = Demo.Pair(Demo.Capability("jira", "jira"));
        _connections = connections;

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(catalog);
        builder.Services.AddSingleton<IDownstreamConnectionManager>(connections);
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(_clientToServer.Reader.AsStream(), _serverToClient.Writer.AsStream())
            .WithTools<OrchestratorTool>()
            .WithRequestFilters(filters => filters.AddCallToolFilter(ToolCallValidationFilter.Attach));

        _host = builder.Build();
        await _host.StartAsync();

        _client = await McpClient.CreateAsync(new StreamClientTransport(
            serverInput: _clientToServer.Writer.AsStream(), serverOutput: _serverToClient.Reader.AsStream()));
    }

    public async Task DisposeAsync()
    {
        if (_client is not null)
        {
            await _client.DisposeAsync();
        }

        if (_host is not null)
        {
            await _host.StopAsync();
            _host.Dispose();
        }

        if (_connections is not null)
        {
            await _connections.DisposeAsync();
        }
    }

    private static string Text(CallToolResult result) => string.Join(
        "\n", result.Content.OfType<TextContentBlock>().Select(b => b.Text));

    /// <summary>Decodes the first content block as JSON and returns its 'error' value.</summary>
    private static string ErrorText(CallToolResult result)
    {
        var first = Assert.IsType<TextContentBlock>(result.Content[0]);
        using var doc = JsonDocument.Parse(first.Text);
        return doc.RootElement.GetProperty("error").GetString()!;
    }

    [Fact]
    public async Task Route_with_args_instead_of_arguments_succeeds_via_alias()
    {
        var result = await _client!.CallToolAsync("route", new Dictionary<string, object?>
        {
            ["capability"] = "jira",
            ["tool"] = "get_issue",
            ["args"] = JsonSerializer.SerializeToElement(new Dictionary<string, string> { ["issueKey"] = "PROJ-1" }),
        });

        var text = Text(result);
        Assert.NotEqual(true, result.IsError);
        Assert.Contains("PROJ-1", text);
        // The rescue is annotated so the model learns the canonical name.
        Assert.Contains("alias", text);
        Assert.Contains("'arguments'", text);
    }

    [Fact]
    public async Task Route_missing_arguments_returns_descriptive_error_not_generic_binding_failure()
    {
        var result = await _client!.CallToolAsync("route", new Dictionary<string, object?>
        {
            ["capability"] = "jira",
            ["tool"] = "get_issue",
        });

        Assert.True(result.IsError);
        var error = ErrorText(result);
        Assert.Contains("Missing required parameter 'arguments'", error);
        Assert.Contains("Expected shape: {capability: string, tool: string, arguments: object}", error);
        Assert.DoesNotContain("An error occurred invoking", error);
    }

    [Fact]
    public async Task Route_downstream_failure_relays_the_downstream_error_text()
    {
        var result = await _client!.CallToolAsync("route", new Dictionary<string, object?>
        {
            ["capability"] = "jira",
            ["tool"] = "no_such_tool",
            ["arguments"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
        });

        // The downstream server's own error text comes through, attributed to the capability —
        // not a generic wrapper.
        var error = ErrorText(result);
        Assert.StartsWith("Downstream capability 'jira' tool 'no_such_tool' failed:", error);
        Assert.DoesNotContain("An error occurred invoking 'route'", error);
    }

    [Fact]
    public async Task Route_missing_required_downstream_parameter_relays_the_downstream_cause()
    {
        // get_issue requires 'issueKey'. The downstream MCP SDK genericizes the resulting
        // ArgumentException to "An error occurred invoking 'get_issue'." on the wire and logs
        // the real cause only to the process's stderr — which the orchestrator must relay,
        // because the calling model cannot read the host's logs.
        var result = await _client!.CallToolAsync("route", new Dictionary<string, object?>
        {
            ["capability"] = "jira",
            ["tool"] = "get_issue",
            ["arguments"] = JsonSerializer.SerializeToElement(new Dictionary<string, string>()),
        });

        // The payload is serialized JSON (with escaped quotes), so assert on the parsed view.
        var first = Assert.IsType<TextContentBlock>(result.Content[0]);
        using var doc = JsonDocument.Parse(first.Text);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("isError").GetBoolean());
        Assert.StartsWith(
            "Downstream capability 'jira' tool 'get_issue' failed:",
            root.GetProperty("text").GetString());

        // The downstream's own error names the missing parameter — it never reaches the wire
        // (the SDK genericizes it), so it must arrive via the captured stderr.
        var stderr = string.Join("\n", root.GetProperty("stderr").EnumerateArray().Select(l => l.GetString()));
        Assert.Contains("issueKey", stderr);
    }
}
