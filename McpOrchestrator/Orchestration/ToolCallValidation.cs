using System.Text;
using System.Text.Json;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Validates and normalizes a tools/call request against the target tool's input schema,
/// before the SDK binds the arguments to the tool method. Without this, a call with a
/// missing or misnamed parameter dies inside the SDK's binding layer with the generic
/// "An error occurred invoking '&lt;tool&gt;'" — which gives a model nothing to self-correct
/// from. This produces an error that names the exact problem ("Missing required parameter
/// 'arguments' …") and rescues predictable parameter-name mistakes (like 'args' for
/// 'arguments') by aliasing them to the canonical name.
/// </summary>
internal static class ToolCallValidation
{
    /// <summary>
    /// Canonical parameter name → synonyms models predictably send instead. A synonym is only
    /// applied when the canonical name is a declared property of the target tool's schema, the
    /// canonical name is absent from the call, and the synonym is not itself a declared
    /// property — so the table is safe to apply to every tool this server exposes.
    /// </summary>
    private static readonly Dictionary<string, string[]> Aliases = new(StringComparer.Ordinal)
    {
        // route's 'arguments' — the mistake actually observed in the wild was 'args'.
        ["arguments"] = ["args", "params", "parameters", "input", "payload"],
        ["capability"] = ["capability_name", "capabilityName", "server"],
        ["tool"] = ["tool_name", "toolName"],
        // get_skill / get_skill_file take 'name' and 'path'.
        ["name"] = ["skill", "skill_name", "skillName"],
        ["path"] = ["file", "file_path", "filePath"],
    };

    /// <summary>
    /// Checks the call's arguments against the tool's JSON input schema. Returns the aliased
    /// (normalized) argument map, the aliases that were applied, and — when the call cannot
    /// succeed — an error message naming the missing/unknown/wrongly-typed parameters and the
    /// expected shape. A schema without declared properties validates nothing.
    /// </summary>
    public static ToolCallValidationResult ValidateAndNormalize(
        JsonElement inputSchema, IDictionary<string, JsonElement>? arguments)
    {
        if (inputSchema.ValueKind != JsonValueKind.Object
            || !inputSchema.TryGetProperty("properties", out var propsElement)
            || propsElement.ValueKind != JsonValueKind.Object)
        {
            return new ToolCallValidationResult(null, arguments, Array.Empty<AppliedAlias>());
        }

        var properties = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var property in propsElement.EnumerateObject())
        {
            properties[property.Name] = property.Value;
        }

        var required = new List<string>();
        if (inputSchema.TryGetProperty("required", out var requiredElement)
            && requiredElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in requiredElement.EnumerateArray())
            {
                if (entry.ValueKind == JsonValueKind.String)
                {
                    required.Add(entry.GetString()!);
                }
            }
        }

        var args = arguments is null
            ? new Dictionary<string, JsonElement>(StringComparer.Ordinal)
            : new Dictionary<string, JsonElement>(arguments, StringComparer.Ordinal);

        var applied = new List<AppliedAlias>();
        foreach (var (canonical, synonyms) in Aliases)
        {
            if (!properties.ContainsKey(canonical) || args.ContainsKey(canonical))
            {
                continue;
            }

            foreach (var synonym in synonyms)
            {
                if (properties.ContainsKey(synonym) || !args.TryGetValue(synonym, out var value))
                {
                    continue;
                }

                args.Remove(synonym);
                args[canonical] = value;
                applied.Add(new AppliedAlias(synonym, canonical));
                break;
            }
        }

        var unknown = args.Keys.Where(k => !properties.ContainsKey(k)).OrderBy(k => k, StringComparer.Ordinal).ToList();
        var missing = required.Where(r => !args.ContainsKey(r)).ToList();
        var typeErrors = new List<string>();
        foreach (var (key, value) in args)
        {
            if (properties.TryGetValue(key, out var propSchema)
                && DeclaredType(propSchema) is { } declared
                && !Matches(declared, value.ValueKind))
            {
                typeErrors.Add($"Parameter '{key}' must be {Article(declared)} (received {Article(ReceivedType(value.ValueKind))}).");
            }
        }

        var error = missing.Count > 0 || unknown.Count > 0 || typeErrors.Count > 0
            ? ComposeError(missing, unknown, typeErrors, properties, required)
            : null;

        return new ToolCallValidationResult(error, args, applied);
    }

    /// <summary>Builds the full error sentence, always ending with the expected shape.</summary>
    private static string ComposeError(
        List<string> missing,
        List<string> unknown,
        List<string> typeErrors,
        Dictionary<string, JsonElement> properties,
        List<string> required)
    {
        var sb = new StringBuilder();
        if (missing.Count > 0)
        {
            sb.Append(missing.Count == 1
                ? $"Missing required parameter {Quote(missing)}"
                : $"Missing required parameters {Quote(missing)}");
            if (unknown.Count > 0)
            {
                sb.Append(unknown.Count == 1
                    ? $" (received unknown parameter {Quote(unknown)})"
                    : $" (received unknown parameters {Quote(unknown)})");
            }

            sb.Append('.');
        }
        else if (unknown.Count > 0)
        {
            sb.Append(unknown.Count == 1
                ? $"Unknown parameter {Quote(unknown)}."
                : $"Unknown parameters {Quote(unknown)}.");
        }

        foreach (var typeError in typeErrors)
        {
            if (sb.Length > 0)
            {
                sb.Append(' ');
            }

            sb.Append(typeError);
        }

        sb.Append(" Expected shape: {");
        sb.Append(string.Join(", ", properties.Select(p =>
            $"{p.Key}{(required.Contains(p.Key) ? string.Empty : "?")}: {DeclaredType(p.Value) ?? "object"}")));
        sb.Append("}.");
        return sb.ToString();
    }

    private static string Quote(IEnumerable<string> names) => string.Join(", ", names.Select(n => $"'{n}'"));

    /// <summary>
    /// The schema-declared type of a property, or null when the schema does not constrain it.
    /// The one untyped property on this server is route's 'arguments' (bound to a raw
    /// <see cref="JsonElement"/>); the shape renderer shows it as 'object', which is what the
    /// tool asks for ("Use {} for no arguments").
    /// </summary>
    private static string? DeclaredType(JsonElement propSchema) =>
        propSchema.ValueKind == JsonValueKind.Object
        && propSchema.TryGetProperty("type", out var type)
        && type.ValueKind == JsonValueKind.String
            ? type.GetString()
            : null;

    private static bool Matches(string declaredType, JsonValueKind kind) => declaredType switch
    {
        "string" => kind == JsonValueKind.String,
        "number" or "integer" => kind == JsonValueKind.Number,
        "boolean" => kind is JsonValueKind.True or JsonValueKind.False,
        "object" => kind == JsonValueKind.Object,
        "array" => kind == JsonValueKind.Array,
        "null" => kind == JsonValueKind.Null,
        _ => true,
    };

    private static string ReceivedType(JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => "string",
        JsonValueKind.Number => "number",
        JsonValueKind.True or JsonValueKind.False => "boolean",
        JsonValueKind.Object => "object",
        JsonValueKind.Array => "array",
        _ => "null",
    };

    private static string Article(string type) => type switch
    {
        "object" or "array" or "integer" => $"an {type}",
        "null" => "null",
        _ => $"a {type}",
    };
}

/// <summary>A parameter-name synonym that was rewritten to its canonical name.</summary>
internal sealed record AppliedAlias(string Synonym, string Canonical);

/// <summary>
/// The outcome of <see cref="ToolCallValidation.ValidateAndNormalize"/>: a descriptive error
/// (null when the call is valid), the normalized argument map to forward, and the aliases
/// that were applied.
/// </summary>
internal sealed record ToolCallValidationResult(
    string? Error,
    IDictionary<string, JsonElement>? NormalizedArguments,
    IReadOnlyList<AppliedAlias> AppliedAliases);
