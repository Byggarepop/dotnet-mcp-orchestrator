using McpOrchestrator.Orchestration.Skills;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillFrontmatterParserTests
{
    [Fact]
    public void Parses_minimal_valid_frontmatter_and_body()
    {
        var ok = SkillFrontmatterParser.TryParse(
            "---\nname: pdf-processing\ndescription: Extract PDF text. Use when handling PDFs.\n---\n\n# Steps\nDo the thing.",
            out var frontmatter, out _);

        Assert.True(ok);
        Assert.Equal("pdf-processing", frontmatter!.Name);
        Assert.Equal("Extract PDF text. Use when handling PDFs.", frontmatter.Description);
        Assert.StartsWith("# Steps", frontmatter.Body);
    }

    [Fact]
    public void Unquotes_values_and_ignores_unknown_keys_comments_and_nested_blocks()
    {
        var ok = SkillFrontmatterParser.TryParse(
            "---\n# a comment\nname: \"my-skill\"\ndescription: 'Does things.'\nlicense: MIT\nmetadata:\n  author: someone\n  version: \"1.0\"\n---\nbody",
            out var frontmatter, out _);

        Assert.True(ok);
        Assert.Equal("my-skill", frontmatter!.Name);
        Assert.Equal("Does things.", frontmatter.Description);
    }

    [Fact]
    public void Handles_crlf_line_endings()
    {
        var ok = SkillFrontmatterParser.TryParse(
            "---\r\nname: a-skill\r\ndescription: Something.\r\n---\r\nbody\r\n",
            out var frontmatter, out _);

        Assert.True(ok);
        Assert.Equal("a-skill", frontmatter!.Name);
        Assert.Equal("body", frontmatter.Body);
    }

    [Theory]
    [InlineData("no frontmatter at all", "missing leading")]
    [InlineData("---\nname: a-skill\ndescription: x.", "missing closing")]
    [InlineData("---\ndescription: x.\n---\nbody", "no 'name'")]
    [InlineData("---\nname: a-skill\n---\nbody", "no 'description'")]
    [InlineData("---\nname: Not-Lowercase\ndescription: x.\n---\nbody", "invalid skill name")]
    [InlineData("---\nname: double--hyphen\ndescription: x.\n---\nbody", "invalid skill name")]
    [InlineData("---\nname: -leading\ndescription: x.\n---\nbody", "invalid skill name")]
    [InlineData("---\nname a-skill\ndescription: x.\n---\nbody", "malformed frontmatter line")]
    public void Rejects_invalid_frontmatter_with_a_reason(string content, string expectedReason)
    {
        var ok = SkillFrontmatterParser.TryParse(content, out _, out var error);

        Assert.False(ok);
        Assert.Contains(expectedReason, error!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Rejects_description_over_the_spec_limit()
    {
        var content = $"---\nname: a-skill\ndescription: {new string('x', 1025)}\n---\nbody";

        Assert.False(SkillFrontmatterParser.TryParse(content, out _, out var error));
        Assert.Contains("1024", error!);
    }

    [Theory]
    [InlineData("pdf-processing", true)]
    [InlineData("a", true)]
    [InlineData("skill-2", true)]
    [InlineData("", false)]
    [InlineData("UPPER", false)]
    [InlineData("trailing-", false)]
    [InlineData("has space", false)]
    [InlineData("has_underscore", false)]
    public void Validates_names_per_the_agent_skills_spec(string name, bool expected)
        => Assert.Equal(expected, SkillFrontmatterParser.IsValidName(name));

    [Fact]
    public void Rejects_names_over_64_characters()
        => Assert.False(SkillFrontmatterParser.IsValidName(new string('a', 65)));
}
