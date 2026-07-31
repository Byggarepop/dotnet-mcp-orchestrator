using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillDiscoveryTests
{
    private static string NewTempDir() =>
        Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"skills-{Guid.NewGuid():N}")).FullName;

    private static void WriteSkill(string root, string name, string? description = "Does something useful.")
    {
        var dir = Directory.CreateDirectory(Path.Combine(root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\nInstructions for {name}.");
    }

    [Fact]
    public void Discovers_skills_recursively_with_files_and_hash()
    {
        var root = NewTempDir();
        try
        {
            WriteSkill(root, "alpha");
            var nested = Path.Combine(root, "team", "beta");
            Directory.CreateDirectory(Path.Combine(nested, "references"));
            File.WriteAllText(Path.Combine(nested, "SKILL.md"), "---\nname: beta\ndescription: Beta skill.\n---\nBeta body.");
            File.WriteAllText(Path.Combine(nested, "references", "notes.md"), "notes");

            var skills = SkillDirectoryScanner.Scan(root, "test", NullLogger.Instance);

            Assert.Equal(2, skills.Count);
            var beta = Assert.Single(skills, s => s.Name == "beta");
            Assert.Equal("Beta skill.", beta.Description);
            Assert.Equal("Beta body.", beta.Body);
            Assert.Contains(beta.Files, f => f.RelativePath == "references/notes.md");
            Assert.Matches("^[0-9a-f]{64}$", beta.Sha256);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Skips_nested_skills_the_outer_one_wins()
    {
        var root = NewTempDir();
        try
        {
            var outer = Path.Combine(root, "outer");
            Directory.CreateDirectory(Path.Combine(outer, "inner"));
            File.WriteAllText(Path.Combine(outer, "SKILL.md"), "---\nname: outer\ndescription: Outer.\n---\nx");
            File.WriteAllText(Path.Combine(outer, "inner", "SKILL.md"), "---\nname: inner\ndescription: Inner.\n---\nx");

            var skills = SkillDirectoryScanner.Scan(root, "test", NullLogger.Instance);

            var outerSkill = Assert.Single(skills);
            Assert.Equal("outer", outerSkill.Name);
            // The outer skill owns its whole subtree, nested SKILL.md included as a plain file.
            Assert.Contains(outerSkill.Files, f => f.RelativePath == "inner/SKILL.md");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Skips_invalid_skills_but_keeps_valid_ones()
    {
        var root = NewTempDir();
        try
        {
            WriteSkill(root, "good-skill");
            var badDir = Directory.CreateDirectory(Path.Combine(root, "bad-skill")).FullName;
            File.WriteAllText(Path.Combine(badDir, "SKILL.md"), "no frontmatter here");
            // Frontmatter name not matching the folder name is invalid per the Agent Skills spec.
            var renamed = Directory.CreateDirectory(Path.Combine(root, "wrong-folder")).FullName;
            File.WriteAllText(Path.Combine(renamed, "SKILL.md"), "---\nname: other-name\ndescription: x.\n---\nx");

            var skills = SkillDirectoryScanner.Scan(root, "test", NullLogger.Instance);

            Assert.Equal("good-skill", Assert.Single(skills).Name);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Missing_root_yields_no_skills_without_throwing()
        => Assert.Empty(SkillDirectoryScanner.Scan(
            Path.Combine(Path.GetTempPath(), $"absent-{Guid.NewGuid():N}"), "test", NullLogger.Instance));

    [Fact]
    public void Oversized_files_are_skipped_but_the_skill_survives()
    {
        var root = NewTempDir();
        try
        {
            WriteSkill(root, "big-skill");
            File.WriteAllBytes(
                Path.Combine(root, "big-skill", "huge.bin"),
                new byte[SkillDirectoryScanner.MaxFileBytes + 1]);

            var skill = Assert.Single(SkillDirectoryScanner.Scan(root, "test", NullLogger.Instance));

            Assert.DoesNotContain(skill.Files, f => f.RelativePath == "huge.bin");
            Assert.Contains(skill.Files, f => f.RelativePath == "SKILL.md");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
