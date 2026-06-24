using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Profiling;

/// <summary>Token cost of one advertised meta-tool definition (name + description + input schema).</summary>
public sealed record MetaToolTokens(string Name, int Tokens);

/// <summary>
/// The orchestrator's <em>resting floor</em>: what the agent pays every turn before any work
/// happens — the server instructions (a "system prompt") plus the always-loaded meta-tool
/// definitions. Nothing else reports this in isolation; that is the point of measuring it.
/// </summary>
public sealed record FloorBreakdown(
    int SystemPromptTokens,
    int MetaToolsTokens,
    IReadOnlyList<MetaToolTokens> Tools)
{
    /// <summary>The resting floor: system prompt + meta-tools, in tokens per turn.</summary>
    public int FloorTokens => SystemPromptTokens + MetaToolsTokens;

    /// <summary>How many meta-tools the orchestrator advertises (currently 3).</summary>
    public int ToolCount => Tools.Count;
}

/// <summary>
/// Measures the resting floor from the orchestrator's <em>real</em> advertised surface — it
/// builds the same MCP server registration the live host uses (<see cref="OrchestratorHost"/>),
/// without a transport, and reads back the exact <see cref="ModelContextProtocol.Protocol.Tool"/>
/// definitions and server instructions the agent would receive. No hand-maintained copy of the
/// tool descriptions to drift out of sync.
/// </summary>
public static class OrchestratorSurface
{
    /// <summary>Measures the resting floor with the given token counter.</summary>
    public static FloorBreakdown MeasureFloor(ITokenCounter counter)
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // Register the SAME service-typed parameters the meta-tools declare (catalog + connection
        // manager). This is what tells the SDK they are DI-injected and not model arguments, so they
        // are excluded from the tool schema. It also matters under Native AOT: an unregistered
        // service parameter makes the tool factory try to build a JSON marshaller for it, which
        // needs JsonTypeInfo it doesn't have and throws. Mirroring the host's registrations keeps the
        // measured surface identical to what the agent actually sees. Neither is ever instantiated.
        services.AddSingleton<ICapabilityCatalog>(
            CapabilityCatalog.FromDescriptors(Array.Empty<CapabilityDescriptor>(), NullLogger.Instance));
        services.AddSingleton<IDownstreamConnectionManager, FloorProbeConnections>();

        services.AddMcpServer().WithTools<OrchestratorTool>();

        using var provider = services.BuildServiceProvider();

        // Serialize each Tool through the SDK's own source-generated type info (AOT/trim-safe) so
        // the counted JSON matches what the client actually receives in tools/list.
        var toolTypeInfo = (JsonTypeInfo<Tool>)McpJsonUtilities.DefaultOptions.GetTypeInfo(typeof(Tool));

        var tools = provider.GetServices<McpServerTool>()
            .Select(t => t.ProtocolTool)
            .OrderBy(t => t.Name, StringComparer.Ordinal)
            .Select(t => new MetaToolTokens(t.Name, counter.Count(JsonSerializer.Serialize(t, toolTypeInfo))))
            .ToList();

        return new FloorBreakdown(
            SystemPromptTokens: counter.Count(ReadServerInstructions(provider)),
            MetaToolsTokens: tools.Sum(t => t.Tokens),
            Tools: tools);
    }

    /// <summary>
    /// The server-level instructions the orchestrator advertises (its "system prompt"). The
    /// orchestrator currently sets none — its guidance lives inside the tool descriptions — so this
    /// is normally empty; reading it from options means the floor stays correct if that ever changes.
    /// </summary>
    private static string ReadServerInstructions(IServiceProvider provider)
    {
        try
        {
            return provider.GetService<IOptions<McpServerOptions>>()?.Value.ServerInstructions ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// A stand-in <see cref="IDownstreamConnectionManager"/> registered only so the meta-tools'
    /// connection-manager parameter is recognised as a DI service during surface introspection. It
    /// is never invoked (the floor measurement does not route anywhere) and is deliberately not
    /// disposable, so the probe's service provider can be disposed synchronously.
    /// </summary>
    private sealed class FloorProbeConnections : IDownstreamConnectionManager
    {
        public Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string capability, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Floor probe does not connect downstream.");

        public Task<CallToolResult> CallToolAsync(
            string capability, string tool, IReadOnlyDictionary<string, object?> arguments, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Floor probe does not connect downstream.");
    }
}
