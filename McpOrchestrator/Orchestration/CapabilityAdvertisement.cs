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
    /// Budget for the whole server-instructions block. Claude Code (observed on 2.1.x,
    /// 2026-07-13) renders roughly 2,048 chars of server instructions and silently truncates the
    /// tail with its own note — an inferred client behavior, not documented protocol, so
    /// re-measure occasionally and adjust. 1,900 leaves a safety margin under that cap. Spent in
    /// priority order: header and every name/summary line first (the minimum viable scent), then
    /// promoted instructions in catalog order; the first entry that does not fit is truncated
    /// with a note and the rest are omitted — a visible cut beats a silent client-side one.
    /// </summary>
    internal const int MaxTotalInstructionsChars = 1_900;

    /// <summary>
    /// Per-capability cap on promoted instructions hoisted into the handshake. Generous enough
    /// for real trigger text (a few paragraphs), small enough that one runaway entry cannot
    /// flood the session context.
    /// </summary>
    internal const int MaxPromotedInstructionsChars = 2_000;

    /// <summary>
    /// Smallest remaining budget worth spending on a truncated promoted entry; below this the
    /// entry is omitted outright (a two-line stub carries no usable trigger text).
    /// </summary>
    private const int MinTruncatedEntryChars = 200;

    /// <summary>Per-capability cap on the summary line inside the server instructions.</summary>
    internal const int MaxSummaryChars = 300;

    /// <summary>Per-capability cap on the summary inside the tool-description suffix.</summary>
    internal const int MaxScentSummaryChars = 120;

    private const string TruncationNote = " … [truncated — call 'list_capabilities' for the full instructions]";

    /// <summary>
    /// Builds the server-level instructions block returned by the MCP initialize handshake, or
    /// <c>null</c> when the catalog is empty (the field is then omitted from the handshake).
    /// </summary>
    public static string? BuildServerInstructions(IReadOnlyList<CapabilityDescriptor> capabilities) =>
        BuildServerInstructions(capabilities, out _);

    /// <summary>
    /// Same as <see cref="BuildServerInstructions(IReadOnlyList{CapabilityDescriptor})"/>, also
    /// reporting the promoted capabilities whose instructions were truncated or omitted because
    /// the block hit <see cref="MaxTotalInstructionsChars"/> (in catalog order) — the caller
    /// should warn, since the trigger text those capabilities were promoted for is (partially)
    /// missing from the handshake.
    /// </summary>
    public static string? BuildServerInstructions(
        IReadOnlyList<CapabilityDescriptor> capabilities, out IReadOnlyList<string> overBudget)
    {
        overBudget = Array.Empty<string>();
        if (capabilities.Count == 0)
        {
            return null;
        }

        // '\n' throughout (not AppendLine): the block must be byte-identical across platforms.
        // Pass 1 — render the unconditional base: the header and one name/summary line per
        // capability. These are the minimum viable scent and are never dropped; the total
        // budget applies to what the promoted instructions may add on top.
        const string header =
            "This orchestrator proxies the downstream MCP capabilities listed below. Call " +
            "'list_capabilities' for the full catalog, 'discover_tools' to see one capability's " +
            "tools and schemas, then 'route' to invoke a tool.\n\nCapabilities:\n";
        var summaryLines = capabilities.Select(c => string.IsNullOrWhiteSpace(c.Summary)
            ? $"- {c.Name}\n"
            : $"- {c.Name}: {Truncate(OneLine(c.Summary), MaxSummaryChars, " …")}\n").ToList();
        var remaining = MaxTotalInstructionsChars - header.Length - summaryLines.Sum(l => l.Length);

        // Pass 2 — assemble, spending the remaining budget on promoted instructions in catalog
        // order (each block sits under its capability's summary line). The first entry that
        // does not fit is truncated with the note; every later promoted entry is omitted.
        var sb = new StringBuilder(header);
        var dropped = new List<string>();
        for (var i = 0; i < capabilities.Count; i++)
        {
            sb.Append(summaryLines[i]);

            var capability = capabilities[i];
            if (!capability.Promote || string.IsNullOrWhiteSpace(capability.Instructions))
            {
                continue;
            }
            if (dropped.Count > 0)
            {
                dropped.Add(capability.Name);
                continue;
            }

            var entry = RenderPromotedEntry(capability);
            if (entry.Length <= remaining)
            {
                sb.Append(entry);
                remaining -= entry.Length;
            }
            else
            {
                dropped.Add(capability.Name);
                if (remaining >= MinTruncatedEntryChars)
                {
                    sb.Append(entry, 0, remaining - TruncationNote.Length - 1)
                        .Append(TruncationNote).Append('\n');
                }
            }
        }

        if (dropped.Count > 0)
        {
            overBudget = dropped;
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>Renders one promoted capability's instructions as an indented block ending in '\n'.</summary>
    private static string RenderPromotedEntry(CapabilityDescriptor capability)
    {
        var text = Truncate(capability.Instructions!.Trim(), MaxPromotedInstructionsChars, TruncationNote);
        var sb = new StringBuilder();
        foreach (var line in text.Split('\n'))
        {
            sb.Append("  ").Append(line.TrimEnd('\r')).Append('\n');
        }
        return sb.ToString();
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
