using System.Text;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Generates the session-start advertisement of the capability catalog. The catalog is otherwise
/// pull-only (the agent must call <c>list_capabilities</c> to see it), which defeats capabilities
/// that should be used proactively — nothing in the agent's context ever mentions them. This
/// pushes the catalog into the two surfaces a client renders without being asked:
/// <list type="bullet">
/// <item>the MCP server-level <c>instructions</c> field of the initialize response — every
/// capability's name + summary, plus the full <c>instructions</c> of capabilities marked
/// <see cref="CapabilityDescriptor.Promote"/>;</item>
/// <item>a "currently registered" suffix appended to the <c>list_capabilities</c> tool
/// description, so even a bare tools/list carries the capability names.</item>
/// </list>
/// </summary>
internal static class CapabilityAdvertisement
{
    /// <summary>
    /// Per-capability cap on promoted instructions hoisted into the handshake. Generous enough
    /// for real trigger text (a few paragraphs), small enough that one runaway entry cannot
    /// flood the session context.
    /// </summary>
    internal const int MaxPromotedInstructionsChars = 2_000;

    /// <summary>Per-capability cap on the summary line inside the server instructions.</summary>
    internal const int MaxSummaryChars = 300;

    /// <summary>Per-capability cap on the summary inside the tool-description suffix.</summary>
    internal const int MaxScentSummaryChars = 120;

    private const string TruncationNote = " … [truncated — call 'list_capabilities' for the full instructions]";

    /// <summary>
    /// Builds the server-level instructions block returned by the MCP initialize handshake, or
    /// <c>null</c> when the catalog is empty (the field is then omitted from the handshake).
    /// </summary>
    public static string? BuildServerInstructions(IReadOnlyList<CapabilityDescriptor> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return null;
        }

        // '\n' throughout (not AppendLine): the block must be byte-identical across platforms.
        var sb = new StringBuilder();
        sb.Append(
            "This orchestrator proxies the downstream MCP capabilities listed below. Call " +
            "'list_capabilities' for the full catalog, 'discover_tools' to see one capability's " +
            "tools and schemas, then 'route' to invoke a tool.\n\nCapabilities:\n");

        foreach (var capability in capabilities)
        {
            sb.Append("- ").Append(capability.Name);
            if (!string.IsNullOrWhiteSpace(capability.Summary))
            {
                sb.Append(": ").Append(Truncate(OneLine(capability.Summary), MaxSummaryChars, " …"));
            }
            sb.Append('\n');

            if (capability.Promote && !string.IsNullOrWhiteSpace(capability.Instructions))
            {
                var text = Truncate(capability.Instructions.Trim(), MaxPromotedInstructionsChars, TruncationNote);
                foreach (var line in text.Split('\n'))
                {
                    sb.Append("  ").Append(line.TrimEnd('\r')).Append('\n');
                }
            }
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// Appends the catalog "scent" — the registered capability names with a shortened summary —
    /// to the <c>list_capabilities</c> tool description. Returns the base description unchanged
    /// when the catalog is empty.
    /// </summary>
    public static string? AppendCatalogScent(string? baseDescription, IReadOnlyList<CapabilityDescriptor> capabilities)
    {
        if (capabilities.Count == 0)
        {
            return baseDescription;
        }

        var entries = capabilities.Select(c => string.IsNullOrWhiteSpace(c.Summary)
            ? c.Name
            : $"{c.Name} ({Truncate(OneLine(c.Summary), MaxScentSummaryChars, "…")})");
        var scent = " Currently registered: " + string.Join("; ", entries) + ".";
        return string.IsNullOrWhiteSpace(baseDescription) ? scent.TrimStart() : baseDescription + scent;
    }

    /// <summary>Collapses a multi-line summary into a single line.</summary>
    private static string OneLine(string text) =>
        string.Join(' ', text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.TrimEnd('\r')));

    private static string Truncate(string text, int maxChars, string note) =>
        text.Length <= maxChars ? text : text[..maxChars] + note;
}
