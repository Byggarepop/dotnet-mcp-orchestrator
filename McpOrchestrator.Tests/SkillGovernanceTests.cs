using System.Text;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillGovernanceTests
{
    private static SkillSnapshot Skill(string name) => new(
        name, $"The {name} skill.", "body", "test",
        [new SkillFile("SKILL.md", Encoding.UTF8.GetBytes($"---\nname: {name}\n---"))]);

    private static SkillCatalog Build(SkillGovernanceOptions governance, params SkillSnapshot[] skills)
        => SkillCatalog.Build(skills, new SkillGovernance(governance), NullLogger.Instance);

    [Fact]
    public void Empty_allow_list_allows_everything()
        => Assert.Equal(2, Build(new SkillGovernanceOptions(), Skill("a"), Skill("b")).Skills.Count);

    [Fact]
    public void Allow_list_filters_and_deny_wins_over_allow()
    {
        var governance = new SkillGovernanceOptions
        {
            AllowedSkills = { "a", "b" },
            DeniedSkills = { "B" }, // Case-insensitive, and deny beats allow.
        };

        var catalog = Build(governance, Skill("a"), Skill("b"), Skill("c"));

        Assert.Equal("a", Assert.Single(catalog.Skills).Name);
    }

    [Fact]
    public void Hash_mismatch_in_warn_mode_serves_the_skill()
    {
        var governance = new SkillGovernanceOptions
        {
            Integrity = { Mode = "warn", Sha256 = { ["a"] = "0000000000000000000000000000000000000000000000000000000000000000" } },
        };

        Assert.Single(Build(governance, Skill("a")).Skills);
    }

    [Fact]
    public void Hash_mismatch_in_block_mode_drops_the_skill()
    {
        var governance = new SkillGovernanceOptions
        {
            Integrity = { Mode = "block", Sha256 = { ["a"] = "0000000000000000000000000000000000000000000000000000000000000000" } },
        };

        Assert.Empty(Build(governance, Skill("a")).Skills);
    }

    [Fact]
    public void Matching_pin_serves_the_skill_even_in_block_mode()
    {
        var skill = Skill("a");
        var governance = new SkillGovernanceOptions
        {
            Integrity = { Mode = "block", Sha256 = { ["a"] = skill.Sha256.ToUpperInvariant() } },
        };

        Assert.Single(Build(governance, skill).Skills);
    }

    [Fact]
    public void Name_collision_first_source_wins_and_lookup_is_case_insensitive()
    {
        var first = Skill("dup");
        var second = new SkillSnapshot("dup", "shadowed", "x", "other", [new SkillFile("SKILL.md", [1])]);

        var catalog = Build(new SkillGovernanceOptions(), first, second);

        Assert.Single(catalog.Skills);
        Assert.True(catalog.TryGet("DUP", out var found));
        Assert.Equal("test", found.SourceId);
    }
}
