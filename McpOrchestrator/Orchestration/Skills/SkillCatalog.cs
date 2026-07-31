using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// The immutable, governance-filtered set of skills currently served. Built by
/// <see cref="SkillsReloadService"/> from all sources' snapshots and swapped atomically into
/// <see cref="SkillRegistry"/> — readers never see a partially-loaded catalog.
/// </summary>
internal sealed class SkillCatalog
{
    /// <summary>A catalog with no skills — the registry's state before the first load.</summary>
    internal static readonly SkillCatalog Empty = new([]);

    private readonly Dictionary<string, SkillSnapshot> _byName;

    private SkillCatalog(List<SkillSnapshot> skills)
    {
        Skills = skills;
        _byName = new Dictionary<string, SkillSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in skills)
        {
            _byName[skill.Name] = skill;
        }
    }

    /// <summary>All served skills, ordered by name.</summary>
    internal IReadOnlyList<SkillSnapshot> Skills { get; }

    /// <summary>Looks a skill up by name (case-insensitive, matching capability-name semantics).</summary>
    internal bool TryGet(string name, out SkillSnapshot skill)
        => _byName.TryGetValue(name, out skill!);

    /// <summary>
    /// Merges per-source snapshots into one catalog: governance filters each skill, and on a
    /// name collision the skill from the earlier source in config order wins (the loser is
    /// logged and dropped — silently shadowing a skill would be a supply-chain hazard).
    /// </summary>
    internal static SkillCatalog Build(
        IEnumerable<SkillSnapshot> discovered,
        SkillGovernance governance,
        ILogger logger)
    {
        var winners = new Dictionary<string, SkillSnapshot>(StringComparer.OrdinalIgnoreCase);
        foreach (var skill in discovered)
        {
            if (winners.TryGetValue(skill.Name, out var existing))
            {
                logger.LogWarning(
                    "skill {Skill} from source {LoserSource} shadowed by source {WinnerSource}; dropped",
                    skill.Name, skill.SourceId, existing.SourceId);
                continue;
            }

            if (!governance.IsAllowed(skill, logger))
            {
                continue;
            }

            winners[skill.Name] = skill;
        }

        return new SkillCatalog(winners.Values.OrderBy(s => s.Name, StringComparer.Ordinal).ToList());
    }
}
