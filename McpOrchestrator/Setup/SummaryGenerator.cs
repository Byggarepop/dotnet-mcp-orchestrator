using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;

namespace McpOrchestrator.Setup;

/// <summary>
/// Derives a one-line routing summary for each imported capability by connecting to its server
/// once — the same connection mechanics <c>profile</c> uses (<see cref="CapabilityCatalog.FromDescriptors"/>
/// + <see cref="DownstreamConnectionManager"/>, so connect timeouts, caching and eviction all apply).
/// Deterministic and offline: the summary comes from the server's own <c>initialize</c>
/// <c>instructions</c> when present, else from its tool names; no LLM involved. A server that
/// fails to start is skipped silently — its capability keeps the TODO placeholder.
/// </summary>
internal static class SummaryGenerator
{
    /// <summary>Generated summaries never exceed this, so the catalog stays scannable one-per-line.</summary>
    private const int MaxLength = 150;

    /// <summary>
    /// Connects to every capability once and returns the generated summary per capability name.
    /// Capabilities whose server fails to start (or yields neither instructions nor tools) are
    /// absent from the result — the caller keeps its existing placeholder for those.
    /// </summary>
    public static async Task<IReadOnlyDictionary<string, string>> GenerateAsync(
        IReadOnlyList<CapabilityDescriptor> capabilities, CancellationToken cancellationToken)
    {
        var summaries = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        var catalog = CapabilityCatalog.FromDescriptors(capabilities, NullLogger.Instance);
        await using var connections = new DownstreamConnectionManager(
            catalog, NullLoggerFactory.Instance, new NullLogger<DownstreamConnectionManager>());

        foreach (var capability in catalog.Capabilities)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var handshake = await connections.GetServerHandshakeAsync(capability.Name, cancellationToken);

                // First non-empty wins: the server's own instructions beat a tool-name template,
                // and the tool list is only fetched when actually needed.
                var summary = FromInstructions(handshake.Instructions);
                if (summary is null)
                {
                    var tools = await connections.ListToolsAsync(capability.Name, cancellationToken);
                    summary = FromToolNames(
                        string.IsNullOrWhiteSpace(handshake.ServerName) ? capability.Name : handshake.ServerName,
                        tools.Select(t => t.Name).ToList());
                }

                if (summary is not null)
                {
                    summaries[capability.Name] = summary;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                // One server refusing to start must not fail (or fail-slow) the whole init —
                // this capability simply keeps the TODO placeholder.
            }
        }

        return summaries;
    }

    /// <summary>
    /// The first sentence of the server's <c>instructions</c>, whitespace-collapsed and truncated
    /// to 150 chars on a word boundary. <c>null</c> when there are no instructions.
    /// </summary>
    internal static string? FromInstructions(string? instructions)
    {
        if (string.IsNullOrWhiteSpace(instructions))
        {
            return null;
        }

        return Truncate(FirstSentence(CollapseWhitespace(instructions)));
    }

    /// <summary>
    /// Tool-name template fallback: <c>"{N} tools for {server}: {up to five names}"</c>, with
    /// <c>", …"</c> appended when more than five exist. <c>null</c> when the server has no tools.
    /// </summary>
    internal static string? FromToolNames(string serverName, IReadOnlyList<string> toolNames)
    {
        if (toolNames.Count == 0)
        {
            return null;
        }

        var shown = string.Join(", ", toolNames.Take(5));
        var more = toolNames.Count > 5 ? ", …" : string.Empty;
        var noun = toolNames.Count == 1 ? "tool" : "tools";
        return Truncate($"{toolNames.Count} {noun} for {serverName}: {shown}{more}");
    }

    /// <summary>
    /// Text up to and including the first '.', '!' or '?' that ends a word (so dotted tokens
    /// like "v2.0" don't cut early); the whole text when no such terminator exists.
    /// </summary>
    private static string FirstSentence(string text)
    {
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '.' or '!' or '?' && (i + 1 == text.Length || char.IsWhiteSpace(text[i + 1])))
            {
                return text[..(i + 1)];
            }
        }

        return text;
    }

    /// <summary>Caps at 150 chars: cut on the last word boundary that fits, then append '…'.</summary>
    private static string Truncate(string text)
    {
        if (text.Length <= MaxLength)
        {
            return text;
        }

        // '…' takes one char, so the kept text may be at most MaxLength - 1 long.
        var cut = text.LastIndexOf(' ', MaxLength - 1);
        var head = cut > 0 ? text[..cut] : text[..(MaxLength - 1)];
        return head.TrimEnd(' ', ',', ';') + "…";
    }

    private static string CollapseWhitespace(string text) =>
        string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
