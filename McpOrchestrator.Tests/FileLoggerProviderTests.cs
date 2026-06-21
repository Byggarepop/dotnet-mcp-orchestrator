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
