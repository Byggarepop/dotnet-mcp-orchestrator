using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// An <see cref="IRoutePlanner"/> decorator that tries a primary planner (e.g. the local LLM)
/// and, if it throws — model not downloaded yet, inference error, etc. — falls back to a second
/// planner (the heuristic). This guarantees the <c>request</c> tool keeps working even when the
/// preferred planner is unavailable.
/// </summary>
public sealed class FallbackRoutePlanner : IRoutePlanner
{
    private readonly IRoutePlanner _primary;
    private readonly IRoutePlanner _fallback;
    private readonly ILogger<FallbackRoutePlanner> _logger;

    /// <summary>Creates the decorator over a primary and a fallback planner.</summary>
    public FallbackRoutePlanner(IRoutePlanner primary, IRoutePlanner fallback, ILogger<FallbackRoutePlanner> logger)
    {
        _primary = primary;
        _fallback = fallback;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<RoutePlan?> PlanAsync(
        string capability,
        IReadOnlyList<McpClientTool> tools,
        string request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _primary.PlanAsync(capability, tools, request, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Primary route planner failed; falling back to the heuristic planner.");
            return await _fallback.PlanAsync(capability, tools, request, cancellationToken);
        }
    }
}
