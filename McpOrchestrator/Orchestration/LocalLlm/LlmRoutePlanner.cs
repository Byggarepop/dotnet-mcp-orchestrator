using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Client;

namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>Abstraction over a single grammar-constrained completion, so the planner can be unit-tested.</summary>
public interface IConstrainedCompleter
{
    /// <summary>Completes <paramref name="userPrompt"/> constrained by the GBNF grammar <paramref name="gbnf"/>.</summary>
    Task<string> CompleteAsync(string systemMessage, string userPrompt, string gbnf, CancellationToken cancellationToken);
}

/// <summary>
/// An <see cref="IRoutePlanner"/> backed by a small local LLM. It routes in two constrained
/// steps: (1) choose a tool, with output limited to the real tool names; (2) extract arguments,
/// with output limited to valid JSON using only the tool's schema keys. Grammar constraints make
/// this reliable even with a sub-1B model. Intended to be wrapped by <see cref="FallbackRoutePlanner"/>
/// so any failure degrades to the heuristic.
/// </summary>
public sealed class LlmRoutePlanner : IRoutePlanner
{
    private readonly IConstrainedCompleter _completer;
    private readonly ILogger<LlmRoutePlanner> _logger;
    private readonly string _modelLabel;

    /// <summary>Creates the planner over a constrained completer (typically <see cref="LocalLlm"/>).</summary>
    public LlmRoutePlanner(IConstrainedCompleter completer, ILogger<LlmRoutePlanner> logger, string modelLabel = "local LLM")
    {
        _completer = completer;
        _logger = logger;
        _modelLabel = modelLabel;
    }

    /// <summary>The minimal view of a tool the planner reasons over (decouples from <see cref="McpClientTool"/>).</summary>
    internal readonly record struct ToolSpec(string Name, string? Description, JsonElement InputSchema);

    /// <inheritdoc />
    public Task<RoutePlan?> PlanAsync(
        string capability,
        IReadOnlyList<McpClientTool> tools,
        string request,
        CancellationToken cancellationToken)
    {
        var specs = tools
            .Select(t => new ToolSpec(t.Name, t.Description, t.ProtocolTool.InputSchema))
            .ToList();
        return PlanCoreAsync(specs, request, cancellationToken);
    }

    /// <summary>
    /// Two-step constrained planning over tool specs. Separated from <see cref="PlanAsync"/> so it
    /// can be unit-tested with a fake completer and synthetic tools.
    /// </summary>
    internal async Task<RoutePlan?> PlanCoreAsync(
        IReadOnlyList<ToolSpec> tools, string request, CancellationToken cancellationToken)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        var chosen = tools.Count == 1
            ? tools[0]
            : await ChooseToolAsync(tools, request, cancellationToken);

        var arguments = await ExtractArgumentsAsync(chosen, request, cancellationToken);

        var rationale = $"{_modelLabel}: selected '{chosen.Name}' and extracted arguments under schema constraint.";
        return new RoutePlan(chosen.Name, arguments, rationale);
    }

    /// <summary>Constrained tool selection: the model must output one of the real tool names.</summary>
    private async Task<ToolSpec> ChooseToolAsync(
        IReadOnlyList<ToolSpec> tools, string request, CancellationToken cancellationToken)
    {
        var names = tools.Select(t => t.Name).ToList();
        var grammar = GbnfGrammar.ToolNameChoice(names);

        var prompt = new StringBuilder();
        prompt.AppendLine("Tools:");
        foreach (var tool in tools)
        {
            prompt.AppendLine($"- {tool.Name}: {tool.Description}");
        }
        prompt.AppendLine().AppendLine($"Request: {request}").Append("Tool name:");

        var answer = await _completer.CompleteAsync(
            "You route a user request to exactly one tool. Reply with only the tool name.",
            prompt.ToString(), grammar, cancellationToken);

        var index = tools.ToList().FindIndex(t => string.Equals(t.Name, answer.Trim(), StringComparison.Ordinal));
        if (index < 0)
        {
            _logger.LogWarning("Tool choice '{Answer}' did not match a known tool; using the first.", answer);
            return tools[0];
        }
        return tools[index];
    }

    /// <summary>Constrained argument extraction: the model must output JSON using only the schema's keys.</summary>
    private async Task<IReadOnlyDictionary<string, object?>> ExtractArgumentsAsync(
        ToolSpec tool, string request, CancellationToken cancellationToken)
    {
        var schema = tool.InputSchema;
        var propertyNames = GbnfGrammar.PropertyNames(schema);
        if (propertyNames.Count == 0)
        {
            return new Dictionary<string, object?>();
        }

        var grammar = GbnfGrammar.JsonObjectWithKeys(propertyNames);

        var prompt = new StringBuilder();
        prompt.AppendLine($"Tool: {tool.Name}");
        prompt.AppendLine("Properties:");
        prompt.AppendLine(DescribeProperties(schema));
        prompt.AppendLine().AppendLine($"Request: {request}").Append("JSON arguments:");

        var json = await _completer.CompleteAsync(
            "Extract the tool arguments as a JSON object using only the documented properties. " +
            "Include every required property. Reply with only JSON.",
            prompt.ToString(), grammar, cancellationToken);

        return ParseArguments(json);
    }

    /// <summary>Renders a schema's properties (name, type, required, description) for the prompt.</summary>
    private static string DescribeProperties(JsonElement schema)
    {
        var required = new HashSet<string>(StringComparer.Ordinal);
        if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in req.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    required.Add(item.GetString()!);
                }
            }
        }

        var sb = new StringBuilder();
        if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                var type = prop.Value.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.String
                    ? t.GetString() : "any";
                var description = prop.Value.TryGetProperty("description", out var d) && d.ValueKind == JsonValueKind.String
                    ? d.GetString() : null;
                var requiredTag = required.Contains(prop.Name) ? "required" : "optional";
                sb.Append($"- {prop.Name} ({type}, {requiredTag})");
                if (!string.IsNullOrWhiteSpace(description))
                {
                    sb.Append($": {description}");
                }
                sb.AppendLine();
            }
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Parses the model's JSON object into an argument map; returns empty on any parse failure.</summary>
    private static IReadOnlyDictionary<string, object?> ParseArguments(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object?>();
            }

            var args = new Dictionary<string, object?>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                args[prop.Name] = prop.Value.Clone();
            }
            return args;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object?>();
        }
    }
}
