using McpOrchestrator.Orchestration.Skills;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillPathValidatorTests
{
    [Theory]
    [InlineData("SKILL.md", "SKILL.md")]
    [InlineData("references/REFERENCE.md", "references/REFERENCE.md")]
    [InlineData("scripts\\extract.py", "scripts/extract.py")]
    [InlineData("assets/nested/deep/file.txt", "assets/nested/deep/file.txt")]
    public void Accepts_plain_relative_paths_and_normalizes_separators(string input, string expected)
    {
        Assert.True(SkillPathValidator.TryNormalize(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../outside.txt")]
    [InlineData("references/../../outside.txt")]
    [InlineData("..\\outside.txt")]
    [InlineData("references\\..\\..\\outside.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share\\file")]
    [InlineData("C:/windows/system32/config")]
    [InlineData("c:\\windows\\notepad.exe")]
    [InlineData("./SKILL.md")]
    [InlineData("references//file.md")]
    [InlineData("%2e%2e/outside.txt")]
    [InlineData("%2E%2E%2Foutside.txt")]
    [InlineData("references/%2e%2e/outside.txt")]
    [InlineData("a%5c..%5cfile")]
    [InlineData("file\0.txt")]
    public void Rejects_traversal_rooted_and_encoded_paths(string? input)
        => Assert.False(SkillPathValidator.TryNormalize(input, out _));
}
