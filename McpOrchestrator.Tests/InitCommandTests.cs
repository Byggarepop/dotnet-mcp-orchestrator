using System.Text.Json;
using System.Text.Json.Nodes;
using McpOrchestrator;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Setup;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers the <c>init</c> subcommand: the pure <see cref="InitCommand.Plan"/> transform (host config
/// in → rewritten host + generated catalog) and the CLI arg/exit-code surface through
/// <see cref="InitCommand.RunAsync"/>.
/// </summary>
public sealed class InitCommandTests
{
    private const string ClaudeStyle = """
        {
          "mcpServers": {
            "files": {
              "command": "npx",
              "args": ["-y", "@modelcontextprotocol/server-filesystem", "/repo"],
              "env": { "FOO": "bar" }
            },
            "jira": {
              "command": "dotnet",
              "args": ["run", "--project", "Jira.csproj"]
            }
          }
        }
        """;

    private static async Task<(int Code, string Out, string Err)> Run(params string[] args)
    {
        var outWriter = new StringWriter();
        var errWriter = new StringWriter();
        var prevOut = Console.Out;
        var prevErr = Console.Error;
        Console.SetOut(outWriter);
        Console.SetError(errWriter);
        try
        {
            var code = await InitCommand.RunAsync(args);
            return (code, outWriter.ToString(), errWriter.ToString());
        }
        finally
        {
            Console.SetOut(prevOut);
            Console.SetError(prevErr);
        }
    }

    private static OrchestratorConfig ParseCatalog(string text) =>
        JsonSerializer.Deserialize(text, OrchestratorConfigJsonContext.Default.OrchestratorConfig)!;

    [Fact]
    public void Plan_imports_every_stdio_server_as_a_capability()
    {
        var plan = InitCommand.Plan(ClaudeStyle, ".mcp.json", "/out/orchestrator.config.json", "mcp-orchestrator");

        Assert.Equal(new[] { "files", "jira" }, plan.ImportedNames);

        var catalog = ParseCatalog(plan.OrchestratorConfigText);
        var files = catalog.Capabilities.Single(c => c.Name == "files");
        Assert.Equal("npx", files.Command);
        Assert.Equal(new[] { "-y", "@modelcontextprotocol/server-filesystem", "/repo" }, files.Args);
        Assert.Equal("bar", files.Env["FOO"]);
        Assert.True(files.Enabled);
        Assert.Equal("stdio", files.Transport);
        // Summary carries a visible TODO placeholder naming the server, for the user to replace.
        Assert.Contains("TODO", files.Summary);
        Assert.Contains("files", files.Summary);
        // Instructions is omitted by default — the summary drives routing.
        Assert.Null(files.Instructions);
    }

    [Fact]
    public void Plan_omits_the_instructions_field_from_the_generated_catalog()
    {
        var plan = InitCommand.Plan(ClaudeStyle, ".mcp.json", "/out/cfg.json", "mcp-orchestrator");
        // No "instructions": property in the JSON body (the header comment may mention the word).
        Assert.DoesNotContain("\"instructions\":", plan.OrchestratorConfigText);
    }

    [Fact]
    public void Plan_with_dev_feed_args_launches_the_orchestrator_from_the_local_feed()
    {
        var devArgs = new[] { "tool", "execute", "McpOrchestrator", "--version", "9.9.9-dev", "--source", "C:/feed", "--yes" };
        var plan = InitCommand.Plan(ClaudeStyle, ".mcp.json", "/out/cfg.json", "dotnet", devArgs);

        var orch = JsonNode.Parse(plan.NewHostConfigText)!["mcpServers"]!["orchestrator"]!.AsObject();
        Assert.Equal("dotnet", orch["command"]!.GetValue<string>());
        Assert.Equal(devArgs, orch["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToArray());
    }

    [Fact]
    public void Plan_rewrites_host_to_only_the_orchestrator_pointing_at_the_catalog()
    {
        var plan = InitCommand.Plan(ClaudeStyle, ".mcp.json", "/out/orchestrator.config.json", "mcp-orchestrator");

        var root = JsonNode.Parse(plan.NewHostConfigText)!.AsObject();
        var servers = root["mcpServers"]!.AsObject();

        Assert.Equal(new[] { "orchestrator" }, servers.Select(kv => kv.Key).ToArray());
        var orch = servers["orchestrator"]!.AsObject();
        Assert.Equal("mcp-orchestrator", orch["command"]!.GetValue<string>());
        Assert.Equal("/out/orchestrator.config.json", orch["env"]!["MCP_ORCHESTRATOR_CONFIG"]!.GetValue<string>());
        // The "mcpServers" shape conventionally omits "type".
        Assert.Null(orch["type"]);
    }

    [Fact]
    public void Plan_preserves_the_servers_shape_and_adds_type_stdio()
    {
        const string vsCode = """
            {
              "inputs": [],
              "servers": {
                "files": { "type": "stdio", "command": "npx", "args": ["-y", "fs"] }
              }
            }
            """;

        var plan = InitCommand.Plan(vsCode, "mcp.json", "/out/cfg.json", "mcp-orchestrator");

        var root = JsonNode.Parse(plan.NewHostConfigText)!.AsObject();
        Assert.True(root.ContainsKey("inputs"));              // unknown top-level keys preserved
        var orch = root["servers"]!["orchestrator"]!.AsObject();
        Assert.Equal("stdio", orch["type"]!.GetValue<string>());
    }

    [Fact]
    public void Plan_leaves_remote_servers_in_place_and_does_not_import_them()
    {
        const string mixed = """
            {
              "servers": {
                "local": { "command": "npx", "args": ["fs"] },
                "remote": { "type": "http", "url": "https://example.com/mcp" }
              }
            }
            """;

        var plan = InitCommand.Plan(mixed, "mcp.json", "/out/cfg.json", "mcp-orchestrator");

        Assert.Equal(new[] { "local" }, plan.ImportedNames);
        Assert.Equal(new[] { "remote" }, plan.SkippedRemote);

        var servers = JsonNode.Parse(plan.NewHostConfigText)!["servers"]!.AsObject();
        Assert.True(servers.ContainsKey("remote"));           // remote untouched
        Assert.True(servers.ContainsKey("orchestrator"));
        Assert.False(servers.ContainsKey("local"));           // imported → removed
    }

    [Fact]
    public void Plan_is_idempotent_dropping_a_prior_orchestrator_entry()
    {
        const string already = """
            {
              "mcpServers": {
                "files": { "command": "npx", "args": ["fs"] },
                "orchestrator": {
                  "command": "mcp-orchestrator",
                  "env": { "MCP_ORCHESTRATOR_CONFIG": "/old/path.json" }
                }
              }
            }
            """;

        var plan = InitCommand.Plan(already, ".mcp.json", "/out/new.json", "mcp-orchestrator");

        // The old orchestrator entry is not imported as a capability...
        Assert.Equal(new[] { "files" }, plan.ImportedNames);
        // ...and the rewritten orchestrator points at the NEW catalog.
        var orch = JsonNode.Parse(plan.NewHostConfigText)!["mcpServers"]!["orchestrator"]!.AsObject();
        Assert.Equal("/out/new.json", orch["env"]!["MCP_ORCHESTRATOR_CONFIG"]!.GetValue<string>());
    }

    [Fact]
    public void Plan_throws_when_no_server_map_present()
    {
        Assert.Throws<InvalidDataException>(() =>
            InitCommand.Plan("""{ "something": 1 }""", "x.json", "/out/c.json", "mcp-orchestrator"));
    }

    // --- CLI surface ---

    [Fact]
    public async Task Help_prints_usage_and_succeeds()
    {
        var (code, stdout, _) = await Run("--help");
        Assert.Equal(0, code);
        Assert.Contains("USAGE", stdout);
        Assert.Contains("adopt", stdout);
    }

    [Fact]
    public async Task No_host_path_is_a_usage_error()
    {
        var (code, _, stderr) = await Run();
        Assert.Equal(1, code);
        Assert.Contains("host config path is required", stderr);
    }

    [Fact]
    public async Task Unknown_flag_is_an_error()
    {
        var (code, _, stderr) = await Run("--bogus", "x.json");
        Assert.Equal(1, code);
        Assert.Contains("unknown option", stderr);
    }

    [Fact]
    public async Task Command_and_dev_feed_together_is_an_error()
    {
        var (code, _, stderr) = await Run("x.json", "--command", "foo", "--dev-feed", "C:/feed");
        Assert.Equal(1, code);
        Assert.Contains("mutually exclusive", stderr);
    }

    [Fact]
    public async Task Missing_host_file_fails_loudly()
    {
        var missing = Path.Combine(Path.GetTempPath(), $"no-host-{Guid.NewGuid():N}.json");
        var (code, _, stderr) = await Run(missing);
        Assert.Equal(1, code);
        Assert.Contains("not found", stderr);
    }

    [Fact]
    public async Task End_to_end_writes_catalog_backs_up_and_rewrites_host()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var hostPath = Path.Combine(dir, ".mcp.json");
        await File.WriteAllTextAsync(hostPath, ClaudeStyle);
        try
        {
            var (code, _, _) = await Run(hostPath);
            Assert.Equal(0, code);

            var catalogPath = Path.Combine(dir, "orchestrator.config.json");
            Assert.True(File.Exists(catalogPath));
            Assert.True(File.Exists(hostPath + ".bak"));

            var catalog = ParseCatalog(await File.ReadAllTextAsync(catalogPath));
            Assert.Equal(2, catalog.Capabilities.Count);

            var orch = JsonNode.Parse(await File.ReadAllTextAsync(hostPath))!["mcpServers"]!["orchestrator"]!.AsObject();
            Assert.Equal(catalogPath, orch["env"]!["MCP_ORCHESTRATOR_CONFIG"]!.GetValue<string>());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task Existing_catalog_without_force_is_an_error()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"init-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        var hostPath = Path.Combine(dir, ".mcp.json");
        await File.WriteAllTextAsync(hostPath, ClaudeStyle);
        await File.WriteAllTextAsync(Path.Combine(dir, "orchestrator.config.json"), "{}");
        try
        {
            var (code, _, stderr) = await Run(hostPath);
            Assert.Equal(1, code);
            Assert.Contains("already exists", stderr);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
