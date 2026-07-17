using McpOrchestrator.Tui.Configuration;
using System.Text.Json;
using Xunit;

namespace McpOrchestrator.Tui.Tests;

public sealed class TuiJsonContextTests
{
    [Theory]
    [InlineData(""" { "registries": [ { "name": "Name", "url": "Url", "note": "test" } ] } """)]
    public void Deserialize_unknown_key_in_registry_entry(string json)
    {
        var result = JsonSerializer.Deserialize(json, TuiJsonContext.Default.OrchestratorConfig);
        var jsonAsString = JsonSerializer.Serialize(result, TuiJsonContext.Default.OrchestratorConfig);

        Assert.Equal("Name", result?.Registries[0].Name.ToString());
        Assert.Equal("Url", result?.Registries[0].Url.ToString());
        Assert.Contains("test", jsonAsString);
    }

    [Theory]
    [InlineData(""" { "capabilities": [ { "name": "CapabilityName", "command": "dotnet", "note": "test" } ] } """)]
    public void Deserialize_unknown_key_in_capability_entry(string json)
    {
        var result = JsonSerializer.Deserialize(json, TuiJsonContext.Default.OrchestratorConfig);
        var jsonAsString = JsonSerializer.Serialize(result, TuiJsonContext.Default.OrchestratorConfig);

        Assert.Equal("CapabilityName", result?.Capabilities[0].Name.ToString());
        Assert.Equal("dotnet", result?.Capabilities[0].Command.ToString());
        Assert.Contains("test", jsonAsString);
    }

    [Theory]
    [InlineData(""" {"//": "note", "capabilities": [ { "name": "CapabilityName", "command": "dotnet"} ] } """)]
    public void Deserialize_unknown_key_in_root_entry(string json)
    {
        var result = JsonSerializer.Deserialize(json, TuiJsonContext.Default.OrchestratorConfig);
        var jsonAsString = JsonSerializer.Serialize(result, TuiJsonContext.Default.OrchestratorConfig);

        Assert.Equal("CapabilityName", result?.Capabilities[0].Name.ToString());
        Assert.Contains("\"capabilities\"", jsonAsString);  // camelCase policy holds
        Assert.Contains('\n', jsonAsString);  // WriteIndented holds
        Assert.Equal("dotnet", result?.Capabilities[0].Command.ToString());
        Assert.Contains("note", jsonAsString);
    }
}
