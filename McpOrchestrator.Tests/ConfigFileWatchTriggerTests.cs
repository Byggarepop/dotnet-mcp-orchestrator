using McpOrchestrator.Orchestration.Reload;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers the file-watch trigger in isolation: debouncing of write bursts and detection of
/// temp-file + atomic-rename writes (which raise no Changed event on the target path).
/// </summary>
public sealed class ConfigFileWatchTriggerTests
{
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task Three_rapid_writes_coalesce_into_exactly_one_callback()
    {
        await InTempConfigAsync(async configPath =>
        {
            var count = 0;
            using var trigger = new ConfigFileWatchTrigger(configPath, QuietPeriod);
            trigger.Start(() =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            });

            for (var i = 0; i < 3; i++)
            {
                await File.WriteAllTextAsync(configPath, $$"""{ "capabilities": [], "rev": {{i}} }""");
            }

            await Wait.ForAssertionAsync(() => Assert.Equal(1, Volatile.Read(ref count)));

            // And it stays at one: no trailing extra fires once the burst has been coalesced.
            await Task.Delay(QuietPeriod * 3);
            Assert.Equal(1, Volatile.Read(ref count));
        });
    }

    [Fact]
    public async Task Atomic_rename_over_the_config_triggers_a_callback()
    {
        await InTempConfigAsync(async configPath =>
        {
            var count = 0;
            using var trigger = new ConfigFileWatchTrigger(configPath, QuietPeriod);
            trigger.Start(() =>
            {
                Interlocked.Increment(ref count);
                return Task.CompletedTask;
            });

            // The temp-file + rename dance editors and tools use for atomic writes.
            var temp = configPath + ".tmp";
            await File.WriteAllTextAsync(temp, """{ "capabilities": [] }""");
            File.Move(temp, configPath, overwrite: true);

            await Wait.ForAssertionAsync(() => Assert.Equal(1, Volatile.Read(ref count)));
        });
    }

    private static async Task InTempConfigAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"watch-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var configPath = Path.Combine(dir, "orchestrator.config.json");
            await File.WriteAllTextAsync(configPath, """{ "capabilities": [] }""");
            await body(configPath);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
