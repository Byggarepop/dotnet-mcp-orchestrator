using System.Text;
using System.Text.Json;
using ModelContextProtocol.Protocol;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Pure conversions between the agent-facing JSON surface and the downstream MCP call
/// shape. Kept free of I/O and dependencies so the tricky parsing rules can be unit-tested
/// directly. Used by the orchestrator tool methods.
/// </summary>
internal static class ToolPayloads
{
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
}
