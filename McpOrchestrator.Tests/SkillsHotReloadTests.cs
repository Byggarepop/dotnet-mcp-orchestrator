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
    public async Task A_refresh_that_changes_skills_runs_the_change_hook_with_the_skill_diff()
    {
        var root = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), $"skills-hook-{Guid.NewGuid():N}")).FullName;
        var registry = new SkillRegistry();
        var hook = new RecordingHook();
        using var service = NewService(registry);
        service.ChangeHook = hook;
        try
        {
            var skillMd = Path.Combine(Directory.CreateDirectory(Path.Combine(root, "alpha")).FullName, "SKILL.md");
            File.WriteAllText(skillMd, "---\nname: alpha\ndescription: One.\n---\nv1");

            // Config-driven apply: the diff is returned to the config pipeline, which announces
            // it together with the capability diff — the service itself stays quiet.
            var applied = await ((ISkillsReloadSink)service).ApplyAsync(DirectoryOptions(root), CancellationToken.None);
            Assert.Equal(new[] { "alpha" }, applied.Added);
            Assert.Empty(hook.Changes);

            // A refresh outside the config pipeline with an actual change: announced by the service.
            File.WriteAllText(skillMd, "---\nname: alpha\ndescription: Two.\n---\nv2");
            File.WriteAllText(
                Path.Combine(Directory.CreateDirectory(Path.Combine(root, "beta")).FullName, "SKILL.md"),
                "---\nname: beta\ndescription: New.\n---\nb");
            var refreshed = await service.RebuildAsync(SkillRebuildTrigger.DirectoryChange, CancellationToken.None);

            Assert.Equal(new[] { "beta" }, refreshed.Added);
            Assert.Equal(new[] { "alpha" }, refreshed.Updated);
            var change = Assert.Single(hook.Changes);
            Assert.Equal("directory-change", change.Trigger);
            Assert.Equal("local", change.Source);
            Assert.Empty(change.Diff.Added);
            Assert.Same(refreshed, change.Diff.Skills);

            // A refresh that changes nothing stays silent.
            await service.RebuildAsync(SkillRebuildTrigger.ScheduledRefresh, CancellationToken.None);
            Assert.Single(hook.Changes);
        }
        finally
        {
            service.Dispose();
            Directory.Delete(root, recursive: true);
        }
    }

    private sealed class RecordingHook : McpOrchestrator.Orchestration.Reload.IConfigChangeHook
    {
        public List<McpOrchestrator.Orchestration.Reload.ConfigChangeEvent> Changes { get; } = new();

        public Task RunAsync(McpOrchestrator.Orchestration.Reload.ConfigChangeEvent change, CancellationToken cancellationToken)
        {
            Changes.Add(change);
            return Task.CompletedTask;
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
