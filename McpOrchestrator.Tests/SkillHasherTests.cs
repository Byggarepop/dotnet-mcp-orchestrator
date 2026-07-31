using System.Text;
using McpOrchestrator.Orchestration.Skills;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillHasherTests
{
    private static SkillFile File(string path, string content) => new(path, Encoding.UTF8.GetBytes(content));

    [Fact]
    public void Hash_is_deterministic_and_independent_of_file_order()
    {
        var a = SkillHasher.ComputeHex([File("SKILL.md", "body"), File("references/r.md", "ref")]);
        var b = SkillHasher.ComputeHex([File("references/r.md", "ref"), File("SKILL.md", "body")]);

        Assert.Equal(a, b);
        Assert.Matches("^[0-9a-f]{64}$", a);
    }

    [Fact]
    public void Hash_changes_when_content_path_or_file_set_changes()
    {
        var baseline = SkillHasher.ComputeHex([File("SKILL.md", "body")]);

        Assert.NotEqual(baseline, SkillHasher.ComputeHex([File("SKILL.md", "other")]));
        Assert.NotEqual(baseline, SkillHasher.ComputeHex([File("OTHER.md", "body")]));
        Assert.NotEqual(baseline, SkillHasher.ComputeHex([File("SKILL.md", "body"), File("extra.md", "")]));
    }

    [Fact]
    public void Path_and_content_boundaries_are_unambiguous()
    {
        // Without separators, ("ab", "c") and ("a", "bc") would collide.
        var a = SkillHasher.ComputeHex([File("ab", "c")]);
        var b = SkillHasher.ComputeHex([File("a", "bc")]);

        Assert.NotEqual(a, b);
    }
}
