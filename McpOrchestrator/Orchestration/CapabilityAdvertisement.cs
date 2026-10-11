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
    /// Budget for the whole server-instructions block. Hosts cap what they render, and the cap
    /// is inferred client behavior rather than documented protocol, so it is re-measured with a
    /// probe MCP server that returns numbered 50-char lines and a headless prompt asking which
    /// marker was last seen. Measured 2026-10-11: Claude Code 2.1.296 keeps 4,096 chars and
    /// appends "… [truncated]" (it kept ~2,048 on 2.1.x in July 2026); GitHub Copilot CLI
    /// 1.0.70 passes 6,000 chars whole once the server is allowlisted for instructions; VS Code's
    /// Copilot agent renders the text verbatim with no fixed cap (read from source). 3,800 keeps
    /// a margin under the tightest measured host. Spent in priority order: the essential
    /// guidance first — the routing header, the skills-discovery hint (when skills are served)
    /// and one name/summary line per capability — then promoted instructions in catalog order;
    /// the first entry that does not fit is truncated with a note and the rest are omitted — a
    /// visible cut beats a silent client-side one.
    /// </summary>
    internal const int MaxTotalInstructionsChars = 3_800;

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

    /// <summary>
    /// Floor for the summary cap when the essential base itself overflows the budget: below this
    /// a summary carries no scent, so the block is allowed to exceed the budget (with a warning)
    /// rather than cut summaries further or drop a capability line or the skills hint.
    /// </summary>
    internal const int MinSummaryChars = 40;

    /// <summary>Per-capability cap on the summary inside the tool-description suffix.</summary>
    internal const int MaxScentSummaryChars = 120;

    private const string TruncationNote = " … [truncated — call 'list_capabilities' for the full instructions]";

    private const string Header =
        "This orchestrator proxies the downstream MCP capabilities below: call " +
        "'list_capabilities' for the catalog, 'discover_tools' for a capability's tool " +
        "schemas, then 'route' to invoke. Example: " +
        "{\"capability\":\"<name>\",\"tool\":\"<tool>\",\"arguments\":{\"key\":\"value\"}} " +
        "— the parameter is 'arguments', not 'args'.\n";

    /// <summary>
    /// The skills-discovery hint for <paramref name="skillCount"/> served skills. Short on
    /// purpose: it names the three catalog tools and nothing else — skill names and bodies are
    /// fetched on demand through those tools, never pushed into the handshake.
    /// </summary>
    public static string SkillsHint(int skillCount) =>
        $"Agent skills are available ({skillCount}): call 'list_skills' to find relevant skills, " +
        "'get_skill' to load one's instructions, and 'get_skill_file' for its supporting files.";

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
        var result = BuildServerInstructions(capabilities, skillsHint: null);
        overBudget = result.OverBudgetPromoted;
        return result.Text;
    }

    /// <summary>
    /// Builds the server-level instructions block with the essential guidance reserved first:
    /// the routing header, <paramref name="skillsHint"/> (<c>null</c> = no skills served, or the
    /// catalog tools are off) and one line per capability are always present; promoted
    /// instructions spend only what remains. With no capabilities the hint stands alone; with
    /// neither, <see cref="ServerInstructions.Text"/> is <c>null</c>.
    /// </summary>
    public static ServerInstructions BuildServerInstructions(
        IReadOnlyList<CapabilityDescriptor> capabilities, string? skillsHint)
    {
        if (capabilities.Count == 0)
        {
            return new ServerInstructions(skillsHint, Array.Empty<string>(), SummariesShortened: false);
        }

        // '\n' throughout (not AppendLine): the block must be byte-identical across platforms.
        // Pass 1 — the unconditional base: header, skills hint, "Capabilities:" and one
        // name/summary line per capability. Its length is measured, not estimated, so the
        // promoted-instructions budget is exactly what the base leaves over.
        // Every line ends in '\n' and the block's final one is trimmed off, so the budget check
        // counts one char less than the rendered lines add up to.
        const int trimmedTrailingNewline = 1;
        var fixedPart = Header + (skillsHint is null ? "" : "\n" + skillsHint + "\n") + "\nCapabilities:\n";
        var summaryCap = MaxSummaryChars;
        var summaryLines = RenderSummaryLines(capabilities, summaryCap);
        var remaining = MaxTotalInstructionsChars + trimmedTrailingNewline - fixedPart.Length - summaryLines.Sum(l => l.Length);

        // The base itself can overflow (many capabilities with long summaries). Summaries are the
        // only optional text in it, so shrink their cap until the base fits — down to a floor,
        // after which the block may exceed the budget: dropping a capability line or the skills
        // hint would be a silent omission, which is the worse failure.
        var shortened = false;
        while (remaining < 0 && summaryCap > MinSummaryChars)
        {
            summaryCap = Math.Max(MinSummaryChars, summaryCap - 20);
            summaryLines = RenderSummaryLines(capabilities, summaryCap);
            remaining = MaxTotalInstructionsChars + trimmedTrailingNewline - fixedPart.Length - summaryLines.Sum(l => l.Length);
            shortened = true;
        }

        // Pass 2 — assemble, spending the remaining budget on promoted instructions in catalog
        // order (each block sits under its capability's summary line). The first entry that
        // does not fit is truncated with the note; every later promoted entry is omitted.
        var sb = new StringBuilder(fixedPart);
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

        return new ServerInstructions(sb.ToString().TrimEnd(), dropped, shortened);
    }

    private static List<string> RenderSummaryLines(IReadOnlyList<CapabilityDescriptor> capabilities, int summaryCap) =>
        capabilities.Select(c => string.IsNullOrWhiteSpace(c.Summary)
            ? $"- {c.Name}\n"
            : $"- {c.Name}: {Truncate(OneLine(c.Summary), summaryCap, " …")}\n").ToList();

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

/// <summary>The rendered server instructions plus what the budget cost.</summary>
/// <param name="Text">The block, or <c>null</c> when there is nothing to advertise.</param>
/// <param name="OverBudgetPromoted">Promoted capabilities whose instructions were truncated or omitted, in catalog order.</param>
/// <param name="SummariesShortened">True when the essential base overflowed and summaries were cut below <see cref="CapabilityAdvertisement.MaxSummaryChars"/>.</param>
internal sealed record ServerInstructions(string? Text, IReadOnlyList<string> OverBudgetPromoted, bool SummariesShortened)
{
    /// <summary>True when the block exceeds <see cref="CapabilityAdvertisement.MaxTotalInstructionsChars"/> even with minimal summaries.</summary>
    public bool ExceedsBudget => (Text?.Length ?? 0) > CapabilityAdvertisement.MaxTotalInstructionsChars;
}
