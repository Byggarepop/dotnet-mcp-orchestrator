using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Applies the governance rules from config to discovered skills: allow/deny lists (deny wins)
/// and SHA-256 integrity pinning with <c>warn</c> or <c>block</c> behavior on mismatch.
/// </summary>
internal sealed class SkillGovernance
{
    private readonly SkillGovernanceOptions _options;
    private readonly bool _block;

    internal SkillGovernance(SkillGovernanceOptions options)
    {
        _options = options;
        _block = string.Equals(options.Integrity.Mode, "block", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Decides whether a discovered skill may be served. Returns false with a reason
    /// (for the log) when it must be dropped.
    /// </summary>
    internal bool IsAllowed(SkillSnapshot skill, ILogger logger)
    {
        if (_options.DeniedSkills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation("skill {Skill} dropped: on the deny list", skill.Name);
            return false;
        }

        if (_options.AllowedSkills.Count > 0 &&
            !_options.AllowedSkills.Contains(skill.Name, StringComparer.OrdinalIgnoreCase))
        {
            logger.LogInformation("skill {Skill} dropped: not on the allow list", skill.Name);
            return false;
        }

        var pinned = _options.Integrity.Sha256
            .FirstOrDefault(p => string.Equals(p.Key, skill.Name, StringComparison.OrdinalIgnoreCase));
        if (pinned.Key is not null &&
            !string.Equals(pinned.Value, skill.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            if (_block)
            {
                logger.LogError(
                    "skill {Skill} BLOCKED: content hash {Actual} does not match pinned {Pinned}",
                    skill.Name, skill.Sha256, pinned.Value);
                return false;
            }

            logger.LogWarning(
                "skill {Skill} served despite hash mismatch (mode=warn): actual {Actual}, pinned {Pinned}",
                skill.Name, skill.Sha256, pinned.Value);
        }

        return true;
    }
}
