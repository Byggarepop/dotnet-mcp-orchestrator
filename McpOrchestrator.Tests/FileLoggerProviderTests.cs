using McpOrchestrator.Diagnostics;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpOrchestrator.Tests;

public class FileLoggerProviderTests
{
    [Fact]
    public void Writes_log_lines_to_orchestrator_log_in_the_given_directory()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcporch-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var provider = FileLoggerProvider.Create(dir))
            {
                Assert.NotNull(provider);
                provider!.CreateLogger("Catalog").LogInformation("hello {Name}", "world");
            } // dispose flushes + closes

            var path = Path.Combine(dir, "orchestrator.log");
            Assert.True(File.Exists(path));
            var text = File.ReadAllText(path);
            Assert.Contains("info", text);
            Assert.Contains("Catalog:", text);
            Assert.Contains("hello world", text);
            Assert.StartsWith("===== McpOrchestrator", text);
            Assert.Contains($"PID {Environment.ProcessId}", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Per_process_file_is_truncated_not_appended()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcporch-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var pidPath = Path.Combine(dir, $"orchestrator-{Environment.ProcessId}.log");
            File.WriteAllText(pidPath, "stale entry from an earlier process with the same PID\n");

            using (var provider = FileLoggerProvider.CreatePerProcess(dir))
            {
                Assert.Equal(pidPath, provider.FilePath);
                provider.CreateLogger("Catalog").LogInformation("fresh");
            }

            var text = File.ReadAllText(pidPath);
            Assert.DoesNotContain("stale entry", text);
            Assert.StartsWith("===== McpOrchestrator", text);
            Assert.Contains("fresh", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Second_instance_falls_back_to_the_per_process_file_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            // FileShare.Read is only enforced by Windows; on Unix the second open succeeds and
            // both instances share orchestrator.log.
            return;
        }

        var dir = Path.Combine(Path.GetTempPath(), "mcporch-logtest-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var pidPath = Path.Combine(dir, $"orchestrator-{Environment.ProcessId}.log");

            using var first = FileLoggerProvider.Create(dir);
            using (var second = FileLoggerProvider.Create(dir))
            {
                Assert.NotNull(second);
                Assert.Equal(pidPath, second!.FilePath);
                second.CreateLogger("Catalog").LogInformation("fresh");
            }

            var text = File.ReadAllText(pidPath);
            Assert.StartsWith("===== McpOrchestrator", text);
            Assert.Contains("fresh", text);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best-effort cleanup */ }
        }
    }

    [Fact]
    public void Create_returns_null_when_disabled()
    {
        Assert.Null(FileLoggerProvider.Create("off"));
    }
}
