using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// The skills reload log must let a reader follow a change from trigger to served catalog
/// without guessing: what triggered the rebuild, which source changed and how, and what each
/// source contributed (and whether that was fresh or cached content).
/// </summary>
public sealed class SkillsRefreshLoggingTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"skills-log-{Guid.NewGuid():N}")).FullName;
    private readonly CollectingLogger _log = new();

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private SkillsReloadService NewService(SkillRegistry registry) => new(
        registry,
        new CapabilityRegistry(CapabilityCatalog.FromDescriptors([], NullLogger.Instance)),
        new WrappingLogger<SkillsReloadService>(_log));

    private string SkillDir(string name)
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, name)).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {name}\ndescription: {name}.\n---\nbody");
        return dir;
    }

    private static SkillsOptions Directory_(string id, string path) => new()
    {
        Sources = { new SkillSourceOptions { Id = id, Type = "directory", Path = path } },
    };

    [Fact]
    public async Task Startup_apply_names_the_trigger_and_each_sources_contribution()
    {
        var registry = new SkillRegistry();
        using var service = NewService(registry);

        await service.ApplyAsync(Directory_("local", SkillDir("a")), SkillRebuildTrigger.Startup, CancellationToken.None);

        var starting = Assert.Single(_log.Entries, e => e.Message.Contains("skills config apply starting"));
        Assert.Contains("trigger=startup", starting.Message);
        Assert.Contains("local:directory", starting.Message);
        var contribution = Assert.Single(_log.Entries, e => e.Message.Contains("skill(s) loaded from"));
        Assert.Contains("1 skill(s) loaded from fresh content", contribution.Message);
        var rebuilt = Assert.Single(_log.Entries, e => e.Message.Contains("skill catalog rebuilt"));
        Assert.Contains("trigger=startup", rebuilt.Message);
        Assert.Contains("local=1 fresh content", rebuilt.Message);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("skill source added"));
    }

    [Fact]
    public async Task Config_change_logs_sources_added_removed_and_changed_before_the_rebuild()
    {
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        var a = SkillDir("a");
        var b = SkillDir("b");
        await service.ApplyAsync(new SkillsOptions
        {
            Sources =
            {
                new SkillSourceOptions { Id = "git2", Type = "directory", Path = a },
                new SkillSourceOptions { Id = "kept", Type = "directory", Path = a },
            },
        }, SkillRebuildTrigger.Startup, CancellationToken.None);

        await service.ApplyAsync(new SkillsOptions
        {
            Sources =
            {
                new SkillSourceOptions { Id = "git3", Type = "directory", Path = a },
                new SkillSourceOptions { Id = "kept", Type = "directory", Path = b, PollSeconds = 60 },
            },
        }, CancellationToken.None); // the ISkillsReloadSink path = config change

        var messages = _log.Entries.Select(e => e.Message).ToList();
        var starting = messages.FindIndex(m => m.Contains("trigger=config-change") && m.Contains("apply starting"));
        var removed = messages.FindIndex(m => m == "skill source removed: git2");
        var added = messages.FindIndex(m => m == "skill source added: git3 (directory)");
        var changed = messages.FindIndex(m => m.StartsWith("skill source changed: kept;", StringComparison.Ordinal));
        var rebuilt = messages.FindLastIndex(m => m.Contains("skill catalog rebuilt"));
        Assert.True(starting >= 0, "apply-starting line missing");
        Assert.True(removed > starting, "removed line missing or out of order");
        Assert.True(added > starting, "added line missing or out of order");
        Assert.True(changed > starting, "changed line missing or out of order");
        Assert.Contains($"path {a} -> {b}", messages[changed]);
        Assert.Contains("pollSeconds default -> 60", messages[changed]);
        Assert.True(rebuilt > changed, "rebuild must follow the change report");
        Assert.Contains("trigger=config-change", messages[rebuilt]);
    }

    [Fact]
    public async Task Credential_changes_are_reported_without_their_values()
    {
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        var a = SkillDir("a");
        await service.ApplyAsync(new SkillsOptions
        {
            Sources = { new SkillSourceOptions { Id = "team", Type = "directory", Path = a, Token = "old-secret" } },
        }, SkillRebuildTrigger.Startup, CancellationToken.None);

        await service.ApplyAsync(new SkillsOptions
        {
            Sources = { new SkillSourceOptions { Id = "team", Type = "directory", Path = a, Token = "new-secret" } },
        }, CancellationToken.None);

        var changed = Assert.Single(_log.Entries, e => e.Message.StartsWith("skill source changed: team;", StringComparison.Ordinal));
        Assert.Contains("token changed", changed.Message);
        Assert.DoesNotContain(_log.Entries, e => e.Message.Contains("secret"));
    }

    [Fact]
    public async Task A_directory_edit_is_attributed_to_the_directory_change_trigger()
    {
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        var a = SkillDir("a");
        await service.ApplyAsync(Directory_("local", a), SkillRebuildTrigger.Startup, CancellationToken.None);

        File.WriteAllText(Path.Combine(a, "SKILL.md"), "---\nname: a\ndescription: edited.\n---\nv2");

        await Wait.ForAssertionAsync(() =>
            Assert.Contains(_log.Entries, e => e.Message.Contains("skill catalog rebuilt") && e.Message.Contains("trigger=directory-change")));
    }

    [Fact]
    public async Task Disabling_skills_is_logged_and_serves_an_empty_catalog()
    {
        var registry = new SkillRegistry();
        using var service = NewService(registry);
        await service.ApplyAsync(Directory_("local", SkillDir("a")), SkillRebuildTrigger.Startup, CancellationToken.None);

        await service.ApplyAsync(null, CancellationToken.None);

        Assert.Contains(_log.Entries, e => e.Message == "skills disabled by this config");
        Assert.Contains(_log.Entries, e => e.Message == "skill source removed: local");
        Assert.Empty(registry.Current.Skills);
        var rebuilt = _log.Entries.Last(e => e.Message.Contains("skill catalog rebuilt"));
        Assert.Contains("0 skill(s) served", rebuilt.Message);
        Assert.Contains("sources: none", rebuilt.Message);
    }

    private sealed class WrappingLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
