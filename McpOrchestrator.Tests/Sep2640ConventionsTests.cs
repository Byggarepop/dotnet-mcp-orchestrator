using System.Text;
using System.Text.Json;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class Sep2640ConventionsTests
{
    [Fact]
    public void Uri_roundtrips_name_and_path()
    {
        var uri = Sep2640Conventions.BuildUri("pdf-processing", "references/REFERENCE.md");

        Assert.Equal("skill://pdf-processing/references/REFERENCE.md", uri);
        Assert.True(Sep2640Conventions.TryParseUri(uri, out var name, out var path));
        Assert.Equal("pdf-processing", name);
        Assert.Equal("references/REFERENCE.md", path);
    }

    [Theory]
    [InlineData("skill://")]
    [InlineData("skill://name-only")]
    [InlineData("skill://name-only/")]
    [InlineData("https://example.com/SKILL.md")]
    [InlineData("")]
    public void Malformed_uris_are_rejected(string uri)
        => Assert.False(Sep2640Conventions.TryParseUri(uri, out _, out _));

    [Fact]
    public void Index_json_lists_each_skill_with_schema_and_uri()
    {
        var skill = new SkillSnapshot(
            "alpha", "Alpha skill.", "body", "test",
            [new SkillFile("SKILL.md", Encoding.UTF8.GetBytes("x"))]);
        var catalog = SkillCatalog.Build(
            [skill], new SkillGovernance(new SkillGovernanceOptions()), NullLogger.Instance);

        var document = JsonSerializer.Deserialize(
            Sep2640Conventions.BuildIndexJson(catalog), SkillIndexJsonContext.Default.SkillIndexDocument);

        Assert.Equal(Sep2640Conventions.IndexSchema, document!.Schema);
        var entry = Assert.Single(document.Skills);
        Assert.Equal("alpha", entry.Name);
        Assert.Equal("skill-md", entry.Type);
        Assert.Equal("skill://alpha/SKILL.md", entry.Url);
    }

    [Theory]
    [InlineData("SKILL.md", "text/markdown", true)]
    [InlineData("references/notes.md", "text/markdown", true)]
    [InlineData("scripts/run.py", "text/x-python", true)]
    [InlineData("assets/logo.png", "image/png", false)]
    [InlineData("assets/data.unknownext", "application/octet-stream", false)]
    public void Mime_types_map_by_extension(string path, string expectedMime, bool expectedText)
    {
        var mime = Sep2640Conventions.GetMimeType(path);

        Assert.Equal(expectedMime, mime);
        Assert.Equal(expectedText, Sep2640Conventions.IsTextMimeType(mime));
    }
}
