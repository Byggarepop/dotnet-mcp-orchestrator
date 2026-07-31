using System.Text;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Skills;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillsToolTests
{
    private static (SkillRegistry Registry, SkillsReloadService Service) Harness(params SkillSnapshot[] skills)
    {
        var registry = new SkillRegistry();
        registry.Swap(SkillCatalog.Build(
            skills, new SkillGovernance(new SkillGovernanceOptions()), NullLogger.Instance));
        var service = new SkillsReloadService(
            registry,
            new CapabilityRegistry(CapabilityCatalog.FromDescriptors([], NullLogger.Instance)),
            NullLogger<SkillsReloadService>.Instance);
        return (registry, service);
    }

    private static SkillSnapshot SampleSkill() => new(
        "pdf-processing",
        "Extract text from PDFs. Use when handling PDF documents.",
        "# PDF processing\nRead references/REFERENCE.md first.",
        "test",
        [
            new SkillFile("SKILL.md", Encoding.UTF8.GetBytes("---\n...")),
            new SkillFile("references/REFERENCE.md", Encoding.UTF8.GetBytes("the reference")),
            new SkillFile("assets/logo.png", [0x89, 0x50, 0x4E, 0x47]),
        ]);

    [Fact]
    public async Task List_skills_returns_names_and_one_line_descriptions()
    {
        var (registry, service) = Harness(SampleSkill());

        var json = await SkillsTool.ListSkills(registry, service, NullLogger<SkillsTool>.Instance);

        using var parsed = JsonDocument.Parse(json);
        var entry = Assert.Single(parsed.RootElement.EnumerateArray());
        Assert.Equal("pdf-processing", entry.GetProperty("name").GetString());
        Assert.StartsWith("Extract text", entry.GetProperty("description").GetString());
    }

    [Fact]
    public async Task Get_skill_returns_body_file_list_and_hash()
    {
        var (registry, service) = Harness(SampleSkill());

        var json = await SkillsTool.GetSkill(registry, service, NullLogger<SkillsTool>.Instance, "pdf-processing");

        using var parsed = JsonDocument.Parse(json);
        Assert.StartsWith("# PDF processing", parsed.RootElement.GetProperty("body").GetString());
        var files = parsed.RootElement.GetProperty("files").EnumerateArray().Select(f => f.GetString()).ToList();
        Assert.Contains("references/REFERENCE.md", files);
        Assert.DoesNotContain("SKILL.md", files); // The body already carries SKILL.md's content.
        Assert.Matches("^[0-9a-f]{64}$", parsed.RootElement.GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Get_skill_for_unknown_name_returns_error_with_available_skills()
    {
        var (registry, service) = Harness(SampleSkill());

        var json = await SkillsTool.GetSkill(registry, service, NullLogger<SkillsTool>.Instance, "nope");

        using var parsed = JsonDocument.Parse(json);
        Assert.Contains("unknown skill", parsed.RootElement.GetProperty("error").GetString());
        Assert.Equal("pdf-processing",
            parsed.RootElement.GetProperty("availableSkills")[0].GetString());
    }

    [Fact]
    public async Task Get_skill_file_serves_text_inline_and_binary_as_base64()
    {
        var (registry, service) = Harness(SampleSkill());

        var text = await SkillsTool.GetSkillFile(
            registry, service, NullLogger<SkillsTool>.Instance, "pdf-processing", "references/REFERENCE.md");
        using (var parsed = JsonDocument.Parse(text))
        {
            Assert.Equal("the reference", parsed.RootElement.GetProperty("text").GetString());
            Assert.False(parsed.RootElement.TryGetProperty("base64", out _));
        }

        var binary = await SkillsTool.GetSkillFile(
            registry, service, NullLogger<SkillsTool>.Instance, "pdf-processing", "assets/logo.png");
        using (var parsed = JsonDocument.Parse(binary))
        {
            Assert.Equal(Convert.ToBase64String(new byte[] { 0x89, 0x50, 0x4E, 0x47 }),
                parsed.RootElement.GetProperty("base64").GetString());
            Assert.False(parsed.RootElement.TryGetProperty("text", out _));
        }
    }

    [Theory]
    [InlineData("../secrets.txt")]
    [InlineData("..\\..\\orchestrator.config.json")]
    [InlineData("/etc/passwd")]
    [InlineData("references/%2e%2e/escape")]
    public async Task Get_skill_file_rejects_traversal_attempts(string path)
    {
        var (registry, service) = Harness(SampleSkill());

        var json = await SkillsTool.GetSkillFile(
            registry, service, NullLogger<SkillsTool>.Instance, "pdf-processing", path);

        using var parsed = JsonDocument.Parse(json);
        Assert.Contains("invalid file path", parsed.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Disabled_catalog_tools_answer_with_an_error()
    {
        var (registry, service) = Harness(SampleSkill());
        await service.ApplyAsync(new SkillsOptions
        {
            Delivery = new SkillDeliveryOptions { CatalogTools = false },
        }, CancellationToken.None);

        var json = await SkillsTool.ListSkills(registry, service, NullLogger<SkillsTool>.Instance);

        using var parsed = JsonDocument.Parse(json);
        Assert.Contains("disabled", parsed.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Descriptions_truncate_at_a_word_boundary()
    {
        var truncated = SkillsTool.TruncateAtWord(
            string.Join(' ', Enumerable.Repeat("word", 60)), SkillsTool.MaxListDescriptionChars);

        Assert.True(truncated.Length <= SkillsTool.MaxListDescriptionChars + 1);
        Assert.EndsWith("…", truncated);
        Assert.DoesNotContain("wor…", truncated); // No mid-word cut.
    }
}
