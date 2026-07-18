using McpOrchestrator.Orchestration;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Tui.Configuration
{
    [JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip)]
    [JsonSerializable(typeof(OrchestratorConfig))]
    internal sealed partial class TuiJsonContext : JsonSerializerContext
    {
    }
}
