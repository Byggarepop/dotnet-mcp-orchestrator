using McpOrchestrator.Orchestration;
using McpOrchestrator.Tui.Configuration;
using System.Text.Json;
using Xunit;

namespace McpOrchestrator.Tui.Tests;

public sealed class ConfigEditorTests
{
    [Fact]
    public void Load_method_path_is_non_existing_file_returns_only_official_registry()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");

        var configEditor = new ConfigEditor();
        var configResult = configEditor.Load(configPath);

        Assert.Single(configResult.Registries);
        Assert.Equal(ConfigEditor.OfficialRegistryUrl, configResult.Registries[0].Url);
    }

    [Fact]
    public void Load_method_config_already_exist_official_url_under_different_name_does_not_add_default()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath, """
        {
          "registries": [
            {
              "name": "official2",
              "url": "https://registry.modelcontextprotocol.io"
            }
          ]
        }
        """);

        var configEditor = new ConfigEditor();
        var configResult = configEditor.Load(configPath);

        Assert.Single(configResult.Registries);
        Assert.Equal(ConfigEditor.OfficialRegistryUrl, configResult.Registries[0].Url);
        Assert.Equal("official2", configResult.Registries[0].Name);
    }

    [Fact]
    public void Load_method_path_contains_file_with_null_returns_JsonException()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath,"");

        var configEditor = new ConfigEditor();

        Assert.Throws<JsonException>(() => configEditor.Load(configPath));
    }

    [Fact]
    public void ResolveConfigPath_env_override_wins()
    {
        var previous = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", @"C:\somewhere\custom.json");

            var resolved = new ConfigEditor().ResolveConfigPath(Directory.GetCurrentDirectory());

            Assert.Equal(@"C:\somewhere\custom.json", resolved);
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", previous);
        }
    }

    [Fact]
    public void ResolveConfigPath_finds_config_in_start_dir_then_ancestors_then_nested()
    {
        var previous = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG");
        try
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", null);
            var root = Directory.CreateTempSubdirectory("mcp-orch-test");
            var child = Directory.CreateDirectory(Path.Combine(root.FullName, "a", "b"));
            var editor = new ConfigEditor();

            // Nothing exists: fall back to the start dir (file created on first save).
            Assert.Equal(Path.Combine(child.FullName, "orchestrator.config.json"),
                editor.ResolveConfigPath(child.FullName));

            // An ancestor config is NOT picked up without a project-root marker —
            // the walk must never escape the project (e.g. into the user profile).
            var nestedDir = Directory.CreateDirectory(Path.Combine(root.FullName, "McpOrchestrator"));
            var nested = Path.Combine(nestedDir.FullName, "orchestrator.config.json");
            File.WriteAllText(nested, "{}");
            Assert.Equal(Path.Combine(child.FullName, "orchestrator.config.json"),
                editor.ResolveConfigPath(child.FullName));

            // With a .git marker at the root, the McpOrchestrator subfolder layout is found.
            Directory.CreateDirectory(Path.Combine(root.FullName, ".git"));
            Assert.Equal(nested, editor.ResolveConfigPath(child.FullName));

            // A config directly in the start dir wins over the ancestor's.
            var direct = Path.Combine(child.FullName, "orchestrator.config.json");
            File.WriteAllText(direct, "{}");
            Assert.Equal(direct, editor.ResolveConfigPath(child.FullName));
        }
        finally
        {
            Environment.SetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG", previous);
        }
    }

    [Fact]
    public void Save_then_load_round_trip_preserves_capabilities_registries_and_unknown_keys()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath, """
        {
          "//": "root note",
          "capabilities": [ { "name": "jira", "command": "npx", "secret": "hidden" } ],
          "registries": [ { "name": "acme", "url": "https://mcp.acme.example" } ]
        }
        """);

        var configEditor = new ConfigEditor();
        var loaded = configEditor.Load(configPath);
        configEditor.Save(loaded, configPath);
        var reloaded = configEditor.Load(configPath);
        var written = File.ReadAllText(configPath);

        Assert.Equal("jira", reloaded.Capabilities[0].Name);
        Assert.Contains(reloaded.Registries, r => r.Name == "acme");
        Assert.Contains("root note", written);
        Assert.Contains("hidden", written);
    }

    [Fact]
    public void Save_over_existing_file_creates_bak_with_previous_content_and_no_temp_files()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        File.WriteAllText(configPath, """ { "capabilities": [ { "name": "old", "command": "npx" } ] } """);

        var configEditor = new ConfigEditor();
        var config = configEditor.Load(configPath);
        config.Capabilities[0].Name = "new";
        configEditor.Save(config, configPath);

        var bakPath = configPath + ".bak";
        Assert.True(File.Exists(bakPath));
        Assert.Contains("old", File.ReadAllText(bakPath));
        Assert.Contains("new", File.ReadAllText(configPath));
        Assert.Equal(2, Directory.GetFiles(dir.FullName).Length);
    }

    [Fact]
    public void First_save_creates_file_without_bak()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");

        var configEditor = new ConfigEditor();
        configEditor.Save(configEditor.Load(configPath), configPath);

        Assert.True(File.Exists(configPath));
        Assert.False(File.Exists(configPath + ".bak"));
        Assert.Single(Directory.GetFiles(dir.FullName));
    }

    [Fact]
    public void Validate_flags_duplicate_names_empty_command_and_invalid_registry_url()
    {
        var config = new OrchestratorConfig
        {
            Capabilities =
            {
                new CapabilityDescriptor { Name = "jira", Command = "npx" },
                new CapabilityDescriptor { Name = "JIRA", Command = "npx" },
                new CapabilityDescriptor { Name = "db", Command = "" },
            },
            Registries = { new RegistrySource { Name = "bad", Url = "not-a-url" } },
        };

        var problems = new ConfigEditor().Validate(config);

        Assert.Equal(3, problems.Count);
        Assert.Contains(problems, p => p.Contains("Duplicate"));
        Assert.Contains(problems, p => p.Contains("no command"));
        Assert.Contains(problems, p => p.Contains("invalid url"));
    }

    [Fact]
    public void Save_of_invalid_config_throws_and_leaves_target_untouched()
    {
        var dir = Directory.CreateTempSubdirectory("mcp-orch-test");
        var configPath = Path.Combine(dir.FullName, "orchestrator.config.json");
        var original = """ { "capabilities": [ { "name": "jira", "command": "npx" } ] } """;
        File.WriteAllText(configPath, original);

        var invalid = new OrchestratorConfig { Capabilities = { new CapabilityDescriptor { Name = "", Command = "" } } };

        Assert.Throws<InvalidOperationException>(() => new ConfigEditor().Save(invalid, configPath));
        Assert.Equal(original, File.ReadAllText(configPath));
        Assert.Single(Directory.GetFiles(dir.FullName));
    }

}
