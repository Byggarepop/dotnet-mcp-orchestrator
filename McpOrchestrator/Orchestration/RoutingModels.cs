using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Orchestration;

/// <summary>Serializes the structured strings the orchestrator returns to the agent.</summary>
public static class OrchestratorJson
{
    /// <summary>
    /// Serializes a value via the source-generated context (Native-AOT safe). The value's static
    /// type must be registered on <see cref="OrchestratorJsonContext"/>.
    /// </summary>
    public static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), OrchestratorJsonContext.Default);
}

/// <summary>
/// Source-generation context for everything the orchestrator writes back to the agent. Indented +
/// camelCase + null-skipping, matching what the agent expects; AOT/trim-safe (no reflection).
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<CapabilityView>))]
[JsonSerializable(typeof(DiscoverView))]
[JsonSerializable(typeof(RouteView))]
[JsonSerializable(typeof(ErrorView))]
internal sealed partial class OrchestratorJsonContext : JsonSerializerContext;

/// <summary>One capability as advertised to the model by <c>list_capabilities</c>.</summary>
public sealed record CapabilityView(string Name, string Summary, string? Instructions);

/// <summary>One downstream tool as reported by <c>discover_tools</c>.</summary>
public sealed record ToolView(string Name, string? Description, JsonElement InputSchema);

/// <summary>The result of <c>discover_tools</c>: a capability and the tools it exposes.</summary>
public sealed record DiscoverView(string Capability, IReadOnlyList<ToolView> Tools);

/// <summary>
/// The result of <c>route</c>: which downstream tool ran and what it returned. <see cref="Text"/>
/// is the flattened text content; <see cref="Structured"/> carries any structured JSON the tool
/// produced.
/// </summary>
public sealed record RouteView
{
    public required string Capability { get; init; }
    public required string Tool { get; init; }
    public bool IsError { get; init; }
    public string? Text { get; init; }
    public JsonElement? Structured { get; init; }

    /// <summary>The arguments actually sent downstream (echoed back so the call is auditable).</summary>
    public JsonNode? Arguments { get; init; }
}

/// <summary>A structured error returned to the model instead of throwing.</summary>
public sealed record ErrorView(string Error, IReadOnlyList<string>? AvailableCapabilities = null);
