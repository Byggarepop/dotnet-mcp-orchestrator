using McpOrchestrator.Orchestration;
using System.Text.Json;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests for <see cref="RegistrySource"/> deserialization and serialization, including case-insensitive property names and round-trip integrity.
/// </summary>
public sealed class RegistrySourceTests
{
    [Theory]
    [InlineData(""" { "registries": [ { "name": "Name", "url": "Url" } ] } """)]
    [InlineData(""" { "REGISTRIES": [ { "name": "Name", "url": "Url" } ] } """)]
    public void Deserialize_OrchestratorConfig_Registry_Name_Url_Match(string json)
    {
        var result = JsonSerializer.Deserialize(json, OrchestratorConfigJsonContext.Default.OrchestratorConfig);

        Assert.Equal("Name", result?.Registries[0].Name.ToString());
        Assert.Equal("Url", result?.Registries[0].Url.ToString());
    }

    [Theory]
    [InlineData(""" { "capabilities": [ { "name": "CapabilityName", "command": "dotnet" } ], "registries": [ { "name": "Name", "url": "Url" } ] }   """)]
    [InlineData(""" { "CAPABILITIES": [ { "name": "CapabilityName", "command": "dotnet" } ], "REGISTRIES": [ { "name": "Name", "url": "Url" } ] }   """)]
    public void Deserialize_OrchestratorConfig_Capabilities_And_Registries(string json)
    {
        var result = JsonSerializer.Deserialize(json, OrchestratorConfigJsonContext.Default.OrchestratorConfig);

        Assert.Equal("CapabilityName", result?.Capabilities[0].Name.ToString());
        Assert.Equal("Name", result?.Registries[0].Name.ToString());
        Assert.Equal("Url", result?.Registries[0].Url.ToString());
    }

    [Theory]
    [InlineData(""" { "capabilities": [ { "name": "CapabilityName", "command": "dotnet" } ] } """)]
    [InlineData(""" { "CAPABILITIES": [ { "name": "CapabilityName", "command": "dotnet" } ] } """)]
    public void Deserialize_OrchestratorConfig_Only_Capabilities(string json)
    {
        var result = JsonSerializer.Deserialize(json, OrchestratorConfigJsonContext.Default.OrchestratorConfig);

        Assert.Equal("CapabilityName", result?.Capabilities[0].Name.ToString());
        Assert.NotNull(result?.Registries);
        Assert.Empty(result.Registries);
    }

    [Fact]
    public void Deserialize_OrchestratorConfig_Registry_Name_Url_Match_From_Object()
    {
        var registrySource = new RegistrySource() { Name = "Name", Url = "Url" };
        var capabilitiesSource = new CapabilityDescriptor() { Name = "CapabilityName", Command = "dotnet" };

        var config = new OrchestratorConfig()
        {
            Registries = { registrySource },
            Capabilities = { capabilitiesSource }
        };

        var configAsJson = JsonSerializer.Serialize(config, OrchestratorConfigJsonContext.Default.OrchestratorConfig);

        var result = JsonSerializer.Deserialize(configAsJson, OrchestratorConfigJsonContext.Default.OrchestratorConfig);

        Assert.Equal("Name", result?.Registries[0].Name.ToString());
        Assert.Equal("Url", result?.Registries[0].Url.ToString());
        Assert.Equal("CapabilityName", result?.Capabilities[0].Name.ToString());
    }

}
