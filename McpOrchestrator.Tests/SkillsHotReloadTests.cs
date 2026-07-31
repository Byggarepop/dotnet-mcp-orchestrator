using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class SkillsHotReloadTests
{
    private static SkillsReloadService NewService(SkillRegistry registry) => new(
        registry,
        new CapabilityRegistry(CapabilityCatalog.FromDescriptors([], NullLogger.Instance)),
        NullLogger<SkillsReloadService>.Instance);

    private static SkillsOptions DirectoryOptions(string root) => new()
    {
        Sources = { new SkillSourceOptions { Id = "local", Type = "directory", Path = root } },
    };

    [Fact]
    public async Task Editing_a_skill_on_disk_is_picked_up_without_restart()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"skills-reload-{Guid.NewGuid():N}")).FullName;
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        try
        {
            var skillDir = Directory.CreateDirectory(Path.Combine(root, "live-skill")).FullName;
            var skillMd = Path.Combine(skillDir, "SKILL.md");
            File.WriteAllText(skillMd, "---\nname: live-skill\ndescription: Version one.\n---\nv1");

            await service.ApplyAsync(DirectoryOptions(root), CancellationToken.None);
            Assert.True(registry.Current.TryGet("live-skill", out var loaded));
            Assert.Equal("v1", loaded.Body);
            var firstHash = loaded.Sha256;

            File.WriteAllText(skillMd, "---\nname: live-skill\ndescription: Version two.\n---\nv2");

            await Wait.ForAssertionAsync(() =>
            {
                Assert.True(registry.Current.TryGet("live-skill", out var reloaded));
                Assert.Equal("v2", reloaded.Body);
                Assert.NotEqual(firstHash, reloaded.Sha256);
            });
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Applying_a_new_config_swaps_governance_and_a_denied_skill_disappears()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"skills-deny-{Guid.NewGuid():N}")).FullName;
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        try
        {
            var skillDir = Directory.CreateDirectory(Path.Combine(root, "some-skill")).FullName;
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: some-skill\ndescription: x.\n---\nx");

            await service.ApplyAsync(DirectoryOptions(root), CancellationToken.None);
            Assert.Single(registry.Current.Skills);

            var denied = DirectoryOptions(root);
            denied.Governance.DeniedSkills.Add("some-skill");
            await service.ApplyAsync(denied, CancellationToken.None);

            Assert.Empty(registry.Current.Skills);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task Applying_null_turns_skills_off()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"skills-off-{Guid.NewGuid():N}")).FullName;
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        try
        {
            var skillDir = Directory.CreateDirectory(Path.Combine(root, "some-skill")).FullName;
            File.WriteAllText(Path.Combine(skillDir, "SKILL.md"), "---\nname: some-skill\ndescription: x.\n---\nx");

            await service.ApplyAsync(DirectoryOptions(root), CancellationToken.None);
            Assert.Single(registry.Current.Skills);

            await service.ApplyAsync(null, CancellationToken.None);

            Assert.Empty(registry.Current.Skills);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }
}
