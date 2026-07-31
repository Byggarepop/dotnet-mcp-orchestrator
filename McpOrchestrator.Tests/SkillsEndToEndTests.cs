using System.IO.Pipelines;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Skills;
using McpOrchestrator.Tools;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// End-to-end: a real MCP server host wired exactly like <c>OrchestratorHost</c>'s skills
/// registration, talked to by a real <see cref="McpClient"/> over in-process pipe streams —
/// exercising both delivery modes (catalog tools and SEP-2640 resources) against a sample
/// skills folder on disk.
/// </summary>
public sealed class SkillsEndToEndTests
{
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Sample_skills_folder_is_served_over_both_delivery_modes()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"skills-e2e-{Guid.NewGuid():N}")).FullName;
        var skillDir = Directory.CreateDirectory(Path.Combine(root, "release-notes")).FullName;
        File.WriteAllText(Path.Combine(skillDir, "SKILL.md"),
            "---\nname: release-notes\ndescription: Writes release notes from commit history.\n---\n# Release notes\nUse references/style.md.");
        Directory.CreateDirectory(Path.Combine(skillDir, "references"));
        File.WriteAllText(Path.Combine(skillDir, "references", "style.md"), "Terse, user-facing.");

        var clientToServer = new Pipe();
        var serverToClient = new Pipe();

        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new CapabilityRegistry(
            CapabilityCatalog.FromDescriptors([], Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)));
        builder.Services.AddSingleton<SkillRegistry>();
        builder.Services.AddSingleton<SkillsReloadService>();
        builder.Services
            .AddMcpServer()
            .WithStreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream())
            .WithTools<SkillsTool>()
            .WithListResourcesHandler(SkillResourceHandlers.ListAsync)
            .WithReadResourceHandler(SkillResourceHandlers.ReadAsync);

        var host = builder.Build();
        try
        {
            await host.StartAsync();
            var skills = host.Services.GetRequiredService<SkillsReloadService>();
            await skills.ApplyAsync(new SkillsOptions
            {
                Sources = { new SkillSourceOptions { Id = "sample", Type = "directory", Path = root } },
            }, CancellationToken.None);

            await using var client = await McpClient.CreateAsync(new StreamClientTransport(
                serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()));

            // Mode A: catalog tools.
            var listResult = await client.CallToolAsync("list_skills", new Dictionary<string, object?>());
            var listJson = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(listResult.Content[0]).Text;
            using (var parsed = JsonDocument.Parse(listJson))
            {
                Assert.Equal("release-notes",
                    Assert.Single(parsed.RootElement.EnumerateArray()).GetProperty("name").GetString());
            }

            var getResult = await client.CallToolAsync("get_skill",
                new Dictionary<string, object?> { ["name"] = "release-notes" });
            var getJson = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(getResult.Content[0]).Text;
            using (var parsed = JsonDocument.Parse(getJson))
            {
                Assert.StartsWith("# Release notes", parsed.RootElement.GetProperty("body").GetString());
            }

            var fileResult = await client.CallToolAsync("get_skill_file",
                new Dictionary<string, object?> { ["name"] = "release-notes", ["path"] = "references/style.md" });
            var fileJson = Assert.IsType<ModelContextProtocol.Protocol.TextContentBlock>(fileResult.Content[0]).Text;
            using (var parsed = JsonDocument.Parse(fileJson))
            {
                Assert.Equal("Terse, user-facing.", parsed.RootElement.GetProperty("text").GetString());
            }

            // Mode B: SEP-2640 resources.
            var resources = await client.ListResourcesAsync();
            Assert.Contains(resources, r => r.Uri == "skill://index.json");
            Assert.Contains(resources, r => r.Uri == "skill://release-notes/SKILL.md");
            Assert.Contains(resources, r => r.Uri == "skill://release-notes/references/style.md");

            var index = await client.ReadResourceAsync("skill://index.json");
            var indexText = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(index.Contents[0]).Text;
            using (var parsed = JsonDocument.Parse(indexText))
            {
                Assert.Equal("release-notes",
                    parsed.RootElement.GetProperty("skills")[0].GetProperty("name").GetString());
            }

            var skillMd = await client.ReadResourceAsync("skill://release-notes/SKILL.md");
            var skillMdContents = Assert.IsType<ModelContextProtocol.Protocol.TextResourceContents>(skillMd.Contents[0]);
            Assert.Equal("text/markdown", skillMdContents.MimeType);
            Assert.Contains("# Release notes", skillMdContents.Text);

            // Traversal is refused on the resource path too.
            await Assert.ThrowsAnyAsync<Exception>(
                async () => await client.ReadResourceAsync("skill://release-notes/../orchestrator.config.json"));
        }
        finally
        {
            await host.StopAsync();
            host.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
