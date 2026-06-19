using System.ComponentModel;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Tools;

/// <summary>
/// The orchestrator's MCP tool surface — the only tools the single agent sees. Together
/// they let one agent reach many downstream MCP servers: discover what capabilities
/// exist, inspect a capability's tools, and dispatch calls to them. The orchestrator
/// connects to the downstream server, invokes the tool, and relays the result back.
/// </summary>
[McpServerToolType]
public sealed class OrchestratorTool
{
    [McpServerTool(Name = "list_capabilities")]
    [Description(
        "List the downstream MCP capabilities this orchestrator can reach (e.g. 'jira', " +
        "'codegen', 'db'). Call this FIRST to find out what is available. Each entry has a " +
        "name, a summary of what it does, and instructions telling you exactly what to pass. " +
        "The reliable path is: read the instructions here, call 'discover_tools' to see a " +
        "capability's tools and their schemas, then call 'route' with the exact tool and " +
        "arguments. Follow each capability's instructions literally (e.g. 'always include the " +
        "Jira issue key') — the orchestrator forwards what you send verbatim and does not " +
        "interpret it.")]
    public static Task<string> ListCapabilities(
        ICapabilityCatalog catalog,
        ILogger<OrchestratorTool> logger)
    {
        logger.LogInformation("list_capabilities ({Count} available)", catalog.Capabilities.Count);

        var views = catalog.Capabilities
            .Select(c => new CapabilityView(c.Name, c.Summary, c.Instructions))
            .ToList();

        return Task.FromResult(OrchestratorJson.Serialize(views));
    }

    [McpServerTool(Name = "discover_tools")]
    [Description(
        "Connect to one downstream capability and list its concrete tools, each with its " +
        "name, description, and JSON input schema. Use this after 'list_capabilities' to " +
        "learn exactly what a capability can do and which arguments each tool needs, before " +
        "calling 'route'.")]
    public static async Task<string> DiscoverTools(
        IDownstreamConnectionManager connections,
        ICapabilityCatalog catalog,
        ILogger<OrchestratorTool> logger,
        [Description("Capability name from 'list_capabilities', e.g. 'jira'.")]
        string capability,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("discover_tools capability={Capability}", capability);
        try
        {
            var tools = await connections.ListToolsAsync(capability, cancellationToken);
            var view = new DiscoverView(
                capability,
                tools.Select(t => new ToolView(t.Name, t.Description, t.ProtocolTool.InputSchema)).ToList());
            return OrchestratorJson.Serialize(view);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "discover_tools failed for capability={Capability}", capability);
            return Error(ex, catalog);
        }
    }

    [McpServerTool(Name = "route")]
    [Description(
        "PREFERRED dispatch tool. Forward a tool call to a downstream capability and return " +
        "its result. You choose the capability and the exact tool name (from " +
        "'discover_tools') and pass an 'arguments' object matching that tool's input schema — " +
        "you do the interpreting, the orchestrator just couriers the call verbatim and relays " +
        "the response. This is the reliable path; use it for all real work. Honor the " +
        "capability's instructions when filling arguments (e.g. always include the Jira issue " +
        "key).")]
    public static async Task<string> Route(
        IDownstreamConnectionManager connections,
        ICapabilityCatalog catalog,
        ILogger<OrchestratorTool> logger,
        [Description("Capability name, e.g. 'jira'.")]
        string capability,
        [Description("Exact downstream tool name as returned by 'discover_tools', e.g. 'get_issue'.")]
        string tool,
        [Description("Arguments object matching the tool's input schema, e.g. {\"issueKey\":\"PROJ-123\"}. Use {} for no arguments.")]
        JsonElement arguments,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("route capability={Capability} tool={Tool}", capability, tool);
        try
        {
            var args = ToArguments(arguments);
            var result = await connections.CallToolAsync(capability, tool, args, cancellationToken);
            return OrchestratorJson.Serialize(ToRouteView(capability, tool, args, result, rationale: null));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "route failed for capability={Capability} tool={Tool}", capability, tool);
            return Error(ex, catalog);
        }
    }

    [McpServerTool(Name = "request")]
    [Description(
        "BEST-EFFORT CONVENIENCE — prefer 'route'. Describe in natural language what you need " +
        "and let the orchestrator GUESS the downstream tool and arguments with a simple " +
        "keyword heuristic (no language understanding). It only works for trivial cases where " +
        "the tool is obvious and the request literally contains the argument values (e.g. an " +
        "explicit 'PROJ-123' key); otherwise it will mis-map your text into the wrong field. " +
        "For anything real, use 'discover_tools' + 'route' and supply the arguments yourself.")]
    public static async Task<string> Request(
        IDownstreamConnectionManager connections,
        IRoutePlanner planner,
        ICapabilityCatalog catalog,
        ILogger<OrchestratorTool> logger,
        [Description("Capability name, e.g. 'jira'.")]
        string capability,
        [Description("Plain-language description of what you need from this capability.")]
        string request,
        CancellationToken cancellationToken)
    {
        logger.LogInformation("request capability={Capability} request={Request}", capability, request);
        try
        {
            var tools = await connections.ListToolsAsync(capability, cancellationToken);
            var plan = await planner.PlanAsync(capability, tools, request, cancellationToken);
            if (plan is null)
            {
                return OrchestratorJson.Serialize(
                    new ErrorView($"Capability '{capability}' exposes no tools to satisfy the request."));
            }

            var result = await connections.CallToolAsync(capability, plan.Tool, plan.Arguments, cancellationToken);
            return OrchestratorJson.Serialize(
                ToRouteView(capability, plan.Tool, plan.Arguments, result, plan.Rationale));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "request failed for capability={Capability}", capability);
            return Error(ex, catalog);
        }
    }

    // ----- helpers -----

    private static readonly IReadOnlyDictionary<string, object?> EmptyArgs =
        new Dictionary<string, object?>();

    private static RouteView ToRouteView(
        string capability,
        string tool,
        IReadOnlyDictionary<string, object?> args,
        CallToolResult result,
        string? rationale) => new()
    {
        Capability = capability,
        Tool = tool,
        IsError = result.IsError ?? false,
        Text = ExtractText(result),
        Structured = result.StructuredContent,
        Arguments = JsonSerializer.SerializeToNode(args, OrchestratorJson.Options),
        Rationale = rationale,
    };

    /// <summary>Flattens a tool result's text content blocks into a single string.</summary>
    private static string ExtractText(CallToolResult result)
    {
        if (result.Content is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Converts the agent-supplied arguments element into a name→value map for the
    /// downstream call. Accepts a JSON object directly, and tolerates an object passed as
    /// a JSON string. Values are cloned so they outlive the source document.
    /// </summary>
    private static IReadOnlyDictionary<string, object?> ToArguments(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
                return dict;

            case JsonValueKind.String:
                var raw = element.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            return ToArguments(doc.RootElement.Clone());
                        }
                    }
                    catch (JsonException)
                    {
                        // Not JSON — fall through to empty.
                    }
                }
                return EmptyArgs;

            default:
                return EmptyArgs;
        }
    }

    private static string Error(Exception ex, ICapabilityCatalog catalog)
    {
        var available = ex is CapabilityNotFoundException notFound ? notFound.Available : catalog.Names;
        return OrchestratorJson.Serialize(new ErrorView(ex.Message, available));
    }
}
