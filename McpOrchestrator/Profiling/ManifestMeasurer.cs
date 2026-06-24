using McpOrchestrator.Orchestration;

namespace McpOrchestrator.Profiling;

/// <summary>
/// The measured manifest cost of one downstream server: the tokens its tool list adds to the
/// agent's context the moment <c>discover_tools</c> pulls it in. <see cref="Reachable"/> is false
/// when the server could not be launched/queried — its size is then unknown (0), and that is
/// surfaced rather than silently treated as free.
/// </summary>
public sealed record ServerManifest(string Name, int Tools, int ManifestTokens, bool Reachable, string? Error);

/// <summary>
/// Measures each downstream server's manifest token cost by actually connecting and serializing
/// the <em>exact</em> payload <c>discover_tools</c> would return (a <see cref="DiscoverView"/> via
/// <see cref="OrchestratorJson"/>). Reusing the real serialization path means the counted number is
/// what the agent would really pay, not an estimate of it.
/// </summary>
public static class ManifestMeasurer
{
    /// <summary>
    /// Connects to every capability in the catalog (in catalog order) and measures its manifest.
    /// Connection failures are captured per-server, never thrown — one unreachable server must not
    /// abort the whole profile.
    /// </summary>
    public static async Task<IReadOnlyList<ServerManifest>> MeasureAsync(
        ICapabilityCatalog catalog,
        IDownstreamConnectionManager connections,
        ITokenCounter counter,
        CancellationToken cancellationToken)
    {
        var results = new List<ServerManifest>(catalog.Capabilities.Count);

        foreach (var capability in catalog.Capabilities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(await MeasureOneAsync(capability.Name, connections, counter, cancellationToken));
        }

        return results;
    }

    private static async Task<ServerManifest> MeasureOneAsync(
        string name,
        IDownstreamConnectionManager connections,
        ITokenCounter counter,
        CancellationToken cancellationToken)
    {
        try
        {
            var tools = await connections.ListToolsAsync(name, cancellationToken);

            // Build the identical view discover_tools returns, then serialize it the identical way,
            // so the token count is of the real on-the-wire manifest.
            var view = new DiscoverView(
                name,
                tools.Select(t => new ToolView(t.Name, t.Description, t.ProtocolTool.InputSchema)).ToList());

            return new ServerManifest(name, tools.Count, counter.Count(OrchestratorJson.Serialize(view)), true, null);
        }
        catch (Exception ex)
        {
            return new ServerManifest(name, Tools: 0, ManifestTokens: 0, Reachable: false, Error: ex.Message);
        }
    }
}
