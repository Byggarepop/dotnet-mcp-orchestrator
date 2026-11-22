using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Pure conversions between the agent-facing JSON surface and the downstream MCP call
/// shape. Kept free of I/O and dependencies so the tricky parsing rules can be unit-tested
/// directly. Used by the orchestrator tool methods.
/// </summary>
internal static class ToolPayloads
{
    /// <summary>
    /// Echoes the sent arguments back as a <see cref="JsonObject"/> for auditing. Built by parsing
    /// each value's raw JSON rather than reflection-serializing an <c>object?</c> map, so it is
    /// Native-AOT safe. Values produced by <see cref="ParseArguments"/> are <see cref="JsonElement"/>s.
    /// </summary>
    public static JsonNode ArgumentsToNode(IReadOnlyDictionary<string, object?> arguments)
    {
        var obj = new JsonObject();
        foreach (var (key, value) in arguments)
        {
            obj[key] = value switch
            {
                JsonElement element => JsonNode.Parse(element.GetRawText()),
                null => null,
                _ => JsonValue.Create(value.ToString()),
            };
        }
        return obj;
    }

    private static readonly IReadOnlyDictionary<string, object?> Empty =
        new Dictionary<string, object?>();

    /// <summary>
    /// Converts the agent-supplied <c>arguments</c> element into a name→value map for the
    /// downstream call. Accepts a JSON object directly, and tolerates an object passed as a
    /// JSON <em>string</em> (some hosts stringify tool arguments). Anything else — a bare
    /// scalar, an array, null, or <c>undefined</c> (the argument omitted) — yields an empty
    /// map. Values are cloned so they outlive the source <see cref="JsonDocument"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, object?> ParseArguments(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                var dict = new Dictionary<string, object?>();
                foreach (var prop in element.EnumerateObject())
                {
                    dict[prop.Name] = prop.Value.Clone();
                }
                return dict;

            case JsonValueKind.String:
                var raw = element.GetString();
                if (!string.IsNullOrWhiteSpace(raw))
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(raw);
                        if (doc.RootElement.ValueKind == JsonValueKind.Object)
                        {
                            return ParseArguments(doc.RootElement.Clone());
                        }
                    }
                    catch (JsonException)
                    {
                        // Not JSON — fall through to empty.
                    }
                }
                return Empty;

            default:
                return Empty;
        }
    }

    /// <summary>
    /// Flattens a tool result's text content blocks into a single newline-joined string.
    /// Non-text blocks (images, audio, embedded resources) are ignored; returns an empty
    /// string when there is no text content.
    /// </summary>
    public static string FlattenText(CallToolResult result)
    {
        if (result.Content is null)
        {
            return string.Empty;
        }

        var sb = new StringBuilder();
        foreach (var block in result.Content)
        {
            if (block is TextContentBlock text)
            {
                if (sb.Length > 0)
                {
                    sb.Append('\n');
                }
                sb.Append(text.Text);
            }
        }
        return sb.ToString();
    }

    /// <summary>
    /// Sends each piece of result data to the model once, in its cheapest form:
    /// <list type="bullet">
    /// <item>Structured content present and the text is JSON equal to it → the text is the
    /// serialized copy of the same data (the MCP compatibility fallback); keep only
    /// <c>structured</c>. JSON text that differs from the structured content is kept.</item>
    /// <item>Structured content holds the text verbatim (the structured content is that string,
    /// or one of its top-level properties is, e.g. <c>{"content": "&lt;text&gt;"}</c> from the
    /// filesystem server) → keep only <c>structured</c>; the text would be sent twice.</item>
    /// <item>No structured content but the text is a JSON object or array → embed it as JSON in
    /// <c>structured</c>. As a string, every quote inside it would be escaped again.</item>
    /// <item>Anything else (plain text, several text blocks, text beside structured data) is kept
    /// as is.</item>
    /// </list>
    /// </summary>
    public static (string? Text, JsonElement? Structured) Compact(string text, JsonElement? structured)
    {
        var parsed = TryParseJsonContainer(text);
        if (structured is not null)
        {
            // Drop the text only when the structured content already carries it; text that
            // differs is kept.
            var duplicate = parsed is { } p
                ? JsonElement.DeepEquals(p, structured.Value)
                : HoldsTextVerbatim(structured.Value, text);
            return duplicate ? (null, structured) : (NullIfEmpty(text), structured);
        }

        return parsed is null ? (NullIfEmpty(text), null) : (null, parsed);
    }

    /// <summary>
    /// True when <paramref name="structured"/> is the string <paramref name="text"/>, or an object
    /// with a top-level string property equal to it.
    /// </summary>
    private static bool HoldsTextVerbatim(JsonElement structured, string text)
    {
        if (text.Length == 0)
        {
            return false;
        }

        switch (structured.ValueKind)
        {
            case JsonValueKind.String:
                return structured.ValueEquals(text);
            case JsonValueKind.Object:
                foreach (var property in structured.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.ValueEquals(text))
                    {
                        return true;
                    }
                }
                return false;
            default:
                return false;
        }
    }

    private static string? NullIfEmpty(string text) => text.Length == 0 ? null : text;

    /// <summary>The text parsed as JSON when it is exactly one JSON object or array; otherwise null.</summary>
    private static JsonElement? TryParseJsonContainer(string text)
    {
        var trimmed = text.AsSpan().TrimStart();
        if (trimmed.IsEmpty || (trimmed[0] != '{' && trimmed[0] != '['))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
