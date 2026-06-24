using McpOrchestrator.Setup;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Unit tests for the shared host-config parser used by both <c>init</c> and
/// <c>profile --host-config</c>: it lifts stdio servers into capabilities, skips remote servers,
/// and ignores a pre-existing orchestrator entry. Pure (no IO), so these are fast.
/// </summary>
public sealed class HostConfigImportTests
{
    [Fact]
    public void Imports_stdio_servers_from_the_mcpServers_shape()
    {
        var result = HostConfigImport.Parse("""
        {
          "mcpServers": {
            "jira":  { "command": "dotnet", "args": ["server.dll", "--persona", "jira"], "env": { "TOKEN": "x" } },
            "files": { "command": "npx", "args": ["-y", "@modelcontextprotocol/server-filesystem", "."], "cwd": "/repo" }
          }
        }
        """);

        Assert.Equal("mcpServers", result.ContainerKey);
        Assert.Equal(new[] { "jira", "files" }, result.Imported);
        Assert.Empty(result.SkippedRemote);

        var jira = result.Capabilities.Single(c => c.Name == "jira");
        Assert.Equal("dotnet", jira.Command);
        Assert.Equal(new[] { "server.dll", "--persona", "jira" }, jira.Args);
        Assert.Equal("x", jira.Env["TOKEN"]);

        // "cwd" (Claude Desktop / Cursor) maps to WorkingDirectory.
        Assert.Equal("/repo", result.Capabilities.Single(c => c.Name == "files").WorkingDirectory);
    }

    [Fact]
    public void Imports_from_the_servers_shape_too()
    {
        var result = HostConfigImport.Parse("""
        {
          "servers": {
            "db": { "type": "stdio", "command": "db-mcp", "workingDirectory": "/srv" }
          }
        }
        """);

        Assert.Equal("servers", result.ContainerKey);
        Assert.Equal(new[] { "db" }, result.Imported);
        Assert.Equal("/srv", result.Capabilities.Single().WorkingDirectory);
    }

    [Fact]
    public void Skips_remote_servers_and_command_less_entries()
    {
        var result = HostConfigImport.Parse("""
        {
          "mcpServers": {
            "local":  { "command": "dotnet", "args": ["s.dll"] },
            "httpsrv": { "type": "http", "url": "https://example.com/mcp" },
            "ssesrv":  { "type": "sse", "url": "https://example.com/sse" },
            "urlsrv":  { "url": "https://example.com/x" },
            "nocmd":   { "args": ["nope"] }
          }
        }
        """);

        Assert.Equal(new[] { "local" }, result.Imported);
        Assert.Equal(new[] { "httpsrv", "ssesrv", "urlsrv", "nocmd" }, result.SkippedRemote);
    }

    [Fact]
    public void Recognizes_and_separates_a_preexisting_orchestrator_entry()
    {
        var result = HostConfigImport.Parse("""
        {
          "mcpServers": {
            "jira": { "command": "dotnet", "args": ["s.dll", "--persona", "jira"] },
            "orchestrator": { "command": "mcp-orchestrator", "env": { "MCP_ORCHESTRATOR_CONFIG": "x.json" } }
          }
        }
        """);

        Assert.Equal(new[] { "jira" }, result.Imported);
        Assert.Equal(new[] { "orchestrator" }, result.OrchestratorKeys);
        Assert.DoesNotContain(result.Capabilities, c => c.Name == "orchestrator");
    }

    [Theory]
    [InlineData("[]", "root must be a JSON object")]
    [InlineData("""{ "tools": {} }""", "no \"mcpServers\" or \"servers\"")]
    [InlineData("""{ "mcpServers": [] }""", "must be a JSON object")]
    public void Rejects_malformed_host_configs(string json, string expectedFragment)
    {
        var ex = Assert.Throws<InvalidDataException>(() => HostConfigImport.Parse(json));
        Assert.Contains(expectedFragment, ex.Message);
    }
}
