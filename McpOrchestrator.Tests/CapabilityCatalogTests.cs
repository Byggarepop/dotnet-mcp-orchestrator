using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests for catalog construction: descriptor validation/dedup (<see cref="CapabilityCatalog.FromDescriptors"/>)
/// and <c>${VAR}</c> substitution during <see cref="CapabilityCatalog.Load"/>.
/// </summary>
public sealed class CapabilityCatalogTests
{
    private static CapabilityDescriptor Cap(string name, string command = "dotnet", bool enabled = true) => new()
    {
        Name = name,
        Command = command,
        Enabled = enabled,
        Summary = "s",
        Instructions = "i",
    };

    [Fact]
    public void FromDescriptors_skips_disabled()
    {
        var catalog = CapabilityCatalog.FromDescriptors(
            new[] { Cap("a"), Cap("b", enabled: false) }, NullLogger.Instance);

        Assert.Equal(new[] { "a" }, catalog.Names);
    }

    [Fact]
    public void FromDescriptors_skips_unnamed_and_commandless()
    {
        var catalog = CapabilityCatalog.FromDescriptors(
            new[] { Cap(""), Cap("nocmd", command: ""), Cap("ok") }, NullLogger.Instance);

        Assert.Equal(new[] { "ok" }, catalog.Names);
    }

    [Fact]
    public void FromDescriptors_dedupes_by_name_case_insensitively_first_wins()
    {
        var first = Cap("Jira", command: "first");
        var dup = Cap("jira", command: "second");

        var catalog = CapabilityCatalog.FromDescriptors(new[] { first, dup }, NullLogger.Instance);

        Assert.Single(catalog.Capabilities);
        Assert.Equal("first", catalog.Find("JIRA")!.Command);
    }

    [Fact]
    public void Find_is_case_insensitive_and_null_safe()
    {
        var catalog = CapabilityCatalog.FromDescriptors(new[] { Cap("jira") }, NullLogger.Instance);

        Assert.NotNull(catalog.Find("JiRa"));
        Assert.Null(catalog.Find("missing"));
        Assert.Null(catalog.Find(null!));
    }

    [Fact]
    public void Load_substitutes_builtin_env_and_leaves_unknown_tokens()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath, """
        {
          "capabilities": [
            {
              "name": "x",
              "summary": "s",
              "instructions": "i",
              "command": "dotnet",
              "args": ["${CONFIG_DIR}/a", "${SOLUTION_DIR}/b", "${TEST_TOKEN_ENV}/c", "${UNKNOWN_ZZZ}/d"]
            }
          ]
        }
        """);

        var previousConfig = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");
        var previousToken = Environment.GetEnvironmentVariable("TEST_TOKEN_ENV");
        try
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", configPath);
            Environment.SetEnvironmentVariable("TEST_TOKEN_ENV", "envval");

            var catalog = CapabilityCatalog.Load(dir.FullName, NullLogger.Instance);

            var args = catalog.Find("x")!.Args;
            Assert.StartsWith(dir.FullName, args[0]);
            Assert.EndsWith("/a", args[0]);
            Assert.DoesNotContain("${", args[1]);          // SOLUTION_DIR resolved
            Assert.EndsWith("/b", args[1]);
            Assert.Equal("envval/c", args[2]);             // env var resolved
            Assert.Equal("${UNKNOWN_ZZZ}/d", args[3]);     // unknown left untouched
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", previousConfig);
            Environment.SetEnvironmentVariable("TEST_TOKEN_ENV", previousToken);
            Directory.Delete(dir.FullName, recursive: true);
        }
    }

    [Fact]
    public void Load_parses_the_shipped_template_with_real_comments()
    {
        // The installed-tool template uses real // comments — verify the loader (JsonCommentHandling
        // .Skip) parses it cleanly and yields an empty catalog (its one example is disabled).
        var templatePath = Path.Combine(Demo.SolutionDir, "McpOrchestrator", "orchestrator.config.template.json");
        Assert.True(File.Exists(templatePath), $"template not found at {templatePath}");

        var previousConfig = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", templatePath);

            var catalog = CapabilityCatalog.Load(Path.GetDirectoryName(templatePath)!, NullLogger.Instance);

            Assert.Empty(catalog.Capabilities); // the example is disabled, so nothing is enabled
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", previousConfig);
        }
    }

    [Fact]
    public void Load_invalid_json_returns_empty_catalog_without_throwing()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-badjson");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath, "{ this is not valid json ");

        var previousConfig = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");
        try
        {
            // The override exists and is chosen first, so the parse failure is exercised here.
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", configPath);

            var catalog = CapabilityCatalog.Load(dir.FullName, NullLogger.Instance);

            Assert.Empty(catalog.Capabilities);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", previousConfig);
            Directory.Delete(dir.FullName, recursive: true);
        }
    }
}
