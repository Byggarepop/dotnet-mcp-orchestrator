using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace ConsafeWorkflow.Mcp.Orchestration;

/// <summary>Shared JSON options for the structured strings the orchestrator returns to the agent.</summary>
public static class OrchestratorJson
{
    /// <summary>Indented, camelCase, null-skipping — readable for a model and easy to parse.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Serializes a value with <see cref="Options"/>.</summary>
    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);
}

/// <summary>One capability as advertised to the model by <c>list_capabilities</c>.</summary>
public sealed record CapabilityView(string Name, string Summary, string Instructions);

/// <summary>One downstream tool as reported by <c>discover_tools</c>.</summary>
public sealed record ToolView(string Name, string? Description, JsonElement InputSchema);

/// <summary>The result of <c>discover_tools</c>: a capability and the tools it exposes.</summary>
public sealed record DiscoverView(string Capability, IReadOnlyList<ToolView> Tools);

/// <summary>
/// The result of <c>route</c>/<c>request</c>: which downstream tool ran and what it
/// returned. <see cref="Text"/> is the flattened text content; <see cref="Structured"/>
/// carries any structured JSON the tool produced.
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

    /// <summary>For <c>request</c>: how the orchestrator chose the tool/arguments. Null for <c>route</c>.</summary>
    public string? Rationale { get; init; }
}

/// <summary>A structured error returned to the model instead of throwing.</summary>
public sealed record ErrorView(string Error, IReadOnlyList<string>? AvailableCapabilities = null);
