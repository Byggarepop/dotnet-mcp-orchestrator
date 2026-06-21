using System.ComponentModel;
using System.Text.Json;
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
    /// <summary>
    /// Tool <c>list_capabilities</c>: returns the configured downstream capabilities (name,
    /// summary, instructions) as JSON. The agent calls this first to discover what it can reach.
    /// </summary>
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

    /// <summary>
    /// Tool <c>discover_tools</c>: connects to one capability and returns its concrete tools
    /// (name, description, JSON input schema) so the agent can pick a tool and build arguments
    /// for <c>route</c>. Returns a structured <see cref="ErrorView"/> if the capability is unknown.
    /// </summary>
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

    /// <summary>
    /// Tool <c>route</c>: forwards a specific tool call — chosen by the agent, with arguments the
    /// agent fills — to a capability and returns the downstream result as a structured
    /// <see cref="RouteView"/>. Exceptions become a structured <see cref="ErrorView"/>.
    /// </summary>
    [McpServerTool(Name = "route")]
    [Description(
        "Forward a tool call to a downstream capability and return its result. You choose the " +
        "capability and the exact tool name (from 'discover_tools') and pass an 'arguments' " +
        "object matching that tool's input schema — you do the interpreting, the orchestrator " +
        "just couriers the call verbatim and relays the response. Honor the capability's " +
        "instructions when filling arguments (e.g. always include the Jira issue key).")]
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
            var args = ToolPayloads.ParseArguments(arguments);
            var result = await connections.CallToolAsync(capability, tool, args, cancellationToken);
            return OrchestratorJson.Serialize(ToRouteView(capability, tool, args, result));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "route failed for capability={Capability} tool={Tool}", capability, tool);
            return Error(ex, catalog);
        }
    }

    // ----- helpers -----

    /// <summary>Builds the structured <see cref="RouteView"/> returned by <c>route</c>.</summary>
    private static RouteView ToRouteView(
        string capability,
        string tool,
        IReadOnlyDictionary<string, object?> args,
        CallToolResult result) => new()
    {
        Capability = capability,
        Tool = tool,
        IsError = result.IsError ?? false,
        Text = ToolPayloads.FlattenText(result),
        Structured = result.StructuredContent,
        Arguments = ToolPayloads.ArgumentsToNode(args),
    };

    /// <summary>
    /// Serializes any exception into a structured <see cref="ErrorView"/> string so the agent
    /// always receives parseable JSON instead of a thrown fault. A
    /// <see cref="CapabilityNotFoundException"/> carries the list of valid names.
    /// </summary>
    private static string Error(Exception ex, ICapabilityCatalog catalog)
    {
        var available = ex is CapabilityNotFoundException notFound ? notFound.Available : catalog.Names;
        return OrchestratorJson.Serialize(new ErrorView(ex.Message, available));
    }
}
