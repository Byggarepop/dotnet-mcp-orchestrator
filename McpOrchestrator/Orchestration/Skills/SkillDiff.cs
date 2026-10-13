namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// What one skill catalog rebuild changed, by skill name: skills that appeared, disappeared, or
/// kept their name but now serve different content (integrity hash or source changed). Handed to
/// the config change hook so a project script can react to skill changes the same way it reacts
/// to capability changes.
/// </summary>
internal sealed record SkillDiff(
    IReadOnlyList<string> Added,
    IReadOnlyList<string> Removed,
    IReadOnlyList<string> Updated,
    IReadOnlyList<string> Sources)
{
    internal static readonly SkillDiff Empty = new([], [], [], []);

    internal bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Updated.Count == 0;

    /// <summary>
    /// Compares two catalogs. Names match case-insensitively (catalog lookup semantics); a skill
    /// counts as updated when its SHA-256 or its source id differs. <c>Sources</c> lists the
    /// distinct source ids of every changed skill, in config order of first appearance.
    /// </summary>
    internal static SkillDiff Compute(SkillCatalog previous, SkillCatalog next)
    {
        var added = new List<string>();
        var removed = new List<string>();
        var updated = new List<string>();
        var sources = new List<string>();

        void Touch(SkillSnapshot skill)
        {
            if (!sources.Contains(skill.SourceId, StringComparer.Ordinal))
            {
                sources.Add(skill.SourceId);
            }
        }

        foreach (var skill in previous.Skills)
        {
            if (!next.TryGet(skill.Name, out _))
            {
                removed.Add(skill.Name);
                Touch(skill);
            }
        }

        foreach (var skill in next.Skills)
        {
            if (!previous.TryGet(skill.Name, out var before))
            {
                added.Add(skill.Name);
                Touch(skill);
            }
            else if (!string.Equals(before.Sha256, skill.Sha256, StringComparison.Ordinal)
                || !string.Equals(before.SourceId, skill.SourceId, StringComparison.Ordinal))
            {
                updated.Add(skill.Name);
                Touch(skill);
            }
        }

        return new SkillDiff(added, removed, updated, sources);
    }
}
