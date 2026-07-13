using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Publishes the capability catalog into the MCP session-start surfaces (see
/// <see cref="CapabilityAdvertisement"/>) just before the stdio session opens: sets
/// <see cref="McpServerOptions.ServerInstructions"/> (read lazily by the SDK when the client's
/// initialize request arrives) and appends the catalog scent to the <c>list_capabilities</c>
/// tool description.
/// </summary>
/// <remarks>
/// Registration order is load-bearing: this must be registered AFTER the config services (so the
/// catalog is loaded — including central mode's initial fetch, which completes in
/// <c>CentralConfigService.StartAsync</c>) and BEFORE the MCP server hosted service (so the text
/// is in place before the client can send initialize). The advertisement is a per-session
/// snapshot: a mid-session hot reload updates <c>list_capabilities</c> results immediately, but
/// the handshake text and tool description were already delivered to the client and are
/// refreshed on the next session.
/// </remarks>
internal sealed class CapabilityAdvertisementService : IHostedService
{
    private readonly IOptions<McpServerOptions> _serverOptions;
    private readonly CapabilityRegistry _registry;
    private readonly ILogger<CapabilityAdvertisementService> _logger;

    public CapabilityAdvertisementService(
        IOptions<McpServerOptions> serverOptions,
        CapabilityRegistry registry,
        ILogger<CapabilityAdvertisementService> logger)
    {
        _serverOptions = serverOptions;
        _registry = registry;
        _logger = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Apply(_serverOptions.Value, _registry.Capabilities);
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Applies the advertisement to the given options (test seam).</summary>
    internal void Apply(McpServerOptions options, IReadOnlyList<CapabilityDescriptor> capabilities)
    {
        options.ServerInstructions = CapabilityAdvertisement.BuildServerInstructions(capabilities, out var overBudget);

        if (options.ToolCollection is { } tools && tools.TryGetPrimitive("list_capabilities", out var tool))
        {
            tool.ProtocolTool.Description =
                CapabilityAdvertisement.AppendCatalogScent(tool.ProtocolTool.Description, capabilities);
        }

        _logger.LogInformation(
            "Advertising {Count} capabilities in the initialize handshake ({Promoted} promoted)",
            capabilities.Count,
            capabilities.Count(c => c.Promote));

        if (overBudget.Count > 0)
        {
            _logger.LogWarning(
                "Advertisement block exceeds {Budget} chars; promoted instructions of {Names} were " +
                "truncated or omitted. Shorten summaries/instructions or promote fewer capabilities.",
                CapabilityAdvertisement.MaxTotalInstructionsChars,
                string.Join(", ", overBudget));
        }
    }
}
