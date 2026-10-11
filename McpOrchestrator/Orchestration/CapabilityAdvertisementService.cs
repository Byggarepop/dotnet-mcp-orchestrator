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
    private readonly Skills.SkillRegistry? _skills;
    private readonly Skills.SkillsReloadService? _skillsConfig;

    /// <param name="skills">Skill registry for the skills-discovery hint; null (e.g. in tests) omits it.</param>
    /// <param name="skillsConfig">Applied skills config, consulted for the catalog-tools delivery flag; null = tools assumed on.</param>
    public CapabilityAdvertisementService(
        IOptions<McpServerOptions> serverOptions,
        CapabilityRegistry registry,
        ILogger<CapabilityAdvertisementService> logger,
        Skills.SkillRegistry? skills = null,
        Skills.SkillsReloadService? skillsConfig = null)
    {
        _serverOptions = serverOptions;
        _registry = registry;
        _logger = logger;
        _skills = skills;
        _skillsConfig = skillsConfig;
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
        // The skills-discovery hint is essential guidance, reserved before any capability text
        // (SkillsReloadService starts before this service, so the registry is populated by now).
        // It is emitted only when there is something to discover AND the catalog tools it names
        // are actually exposed — telling an agent to call a disabled tool is worse than silence.
        var skillCount = _skills?.Current.Skills.Count ?? 0;
        var catalogToolsOn = _skillsConfig?.Delivery.CatalogTools ?? true;
        var skillsHint = skillCount > 0 && catalogToolsOn ? CapabilityAdvertisement.SkillsHint(skillCount) : null;
        if (skillCount > 0 && !catalogToolsOn)
        {
            _logger.LogInformation(
                "skills-discovery hint omitted from the handshake: {Count} skill(s) served but delivery.catalogTools is off",
                skillCount);
        }

        var built = CapabilityAdvertisement.BuildServerInstructions(capabilities, skillsHint);
        options.ServerInstructions = built.Text;

        if (options.ToolCollection is { } tools && tools.TryGetPrimitive("list_capabilities", out var tool))
        {
            tool.ProtocolTool.Description =
                CapabilityAdvertisement.AppendCatalogScent(tool.ProtocolTool.Description, capabilities);
        }

        _logger.LogInformation(
            "Advertising {Count} capabilities in the initialize handshake ({Promoted} promoted, skills hint {Hint}, {Length} chars)",
            capabilities.Count,
            capabilities.Count(c => c.Promote),
            skillsHint is null ? "omitted" : "included",
            built.Text?.Length ?? 0);

        if (built.SummariesShortened)
        {
            _logger.LogWarning(
                "Advertisement base (header, skills hint, {Count} capability lines) exceeds {Budget} chars; " +
                "capability summaries were shortened to fit. Shorten summaries in the config.",
                capabilities.Count, CapabilityAdvertisement.MaxTotalInstructionsChars);
        }

        if (built.ExceedsBudget)
        {
            _logger.LogWarning(
                "Advertisement block is {Length} chars even with minimal summaries, over the {Budget} budget; " +
                "clients that truncate server instructions may cut it. Reduce the number of capabilities.",
                built.Text!.Length, CapabilityAdvertisement.MaxTotalInstructionsChars);
        }

        if (built.OverBudgetPromoted.Count > 0)
        {
            _logger.LogWarning(
                "Advertisement block exceeds {Budget} chars; promoted instructions of {Names} were " +
                "truncated or omitted. Shorten summaries/instructions or promote fewer capabilities.",
                CapabilityAdvertisement.MaxTotalInstructionsChars,
                string.Join(", ", built.OverBudgetPromoted));
        }
    }
}
