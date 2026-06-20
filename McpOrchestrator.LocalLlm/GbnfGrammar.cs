using System.Text;
using System.Text.Json;

namespace McpOrchestrator.Orchestration.LocalLlm;

/// <summary>
/// Builds GBNF grammars that constrain the local model's output, so even a tiny model can only
/// emit a valid tool name or a JSON object whose keys come from a tool's input schema. This is
/// what makes sub-1B models reliable for routing: the decoder is physically prevented from
/// producing anything off-grammar.
/// </summary>
internal static class GbnfGrammar
{
    /// <summary>The root rule name shared by the grammars produced here.</summary>
    public const string Root = "root";

    /// <summary>
    /// A grammar whose only valid outputs are exactly the given tool names (bare, unquoted).
    /// Used to force the tool-selection step to return one of the real tools.
    /// </summary>
    public static string ToolNameChoice(IReadOnlyList<string> toolNames)
    {
        if (toolNames.Count == 0)
        {
            // Degenerate: match the empty string. The caller handles "no tools" before this.
            return "root ::= \"\"";
        }

        var alternatives = string.Join(" | ", toolNames.Select(n => Literal(n)));
        return $"root ::= {alternatives}";
    }

    /// <summary>
    /// A grammar matching a JSON object whose keys are restricted to <paramref name="propertyNames"/>
    /// (any subset, any order) with JSON values. Guarantees valid JSON and prevents hallucinated keys.
    /// </summary>
    public static string JsonObjectWithKeys(IReadOnlyList<string> propertyNames)
    {
        var sb = new StringBuilder();

        if (propertyNames.Count == 0)
        {
            sb.AppendLine("root ::= \"{\" ws \"}\"");
        }
        else
        {
            sb.AppendLine("root ::= \"{\" ws ( pair (ws \",\" ws pair)* )? ws \"}\"");
            sb.AppendLine("pair ::= key ws \":\" ws value");
            var keys = string.Join(" | ", propertyNames.Select(p => JsonStringLiteral(p)));
            sb.AppendLine($"key ::= {keys}");
        }

        // Shared JSON value rules.
        sb.AppendLine("value ::= object | array | string | number | \"true\" | \"false\" | \"null\"");
        sb.AppendLine("object ::= \"{\" ws ( anypair (ws \",\" ws anypair)* )? ws \"}\"");
        sb.AppendLine("anypair ::= string ws \":\" ws value");
        sb.AppendLine("array ::= \"[\" ws ( value (ws \",\" ws value)* )? ws \"]\"");
        sb.AppendLine("string ::= \"\\\"\" char* \"\\\"\"");
        sb.AppendLine("char ::= [^\"\\\\] | \"\\\\\" [\"\\\\/bfnrt]");
        sb.AppendLine("number ::= \"-\"? int frac?");
        sb.AppendLine("int ::= \"0\" | [1-9] [0-9]*");
        sb.AppendLine("frac ::= \".\" [0-9]+");
        sb.Append("ws ::= [ \\t\\n]*");

        return sb.ToString();
    }

    /// <summary>Reads the top-level property names declared in a JSON input schema.</summary>
    public static IReadOnlyList<string> PropertyNames(JsonElement schema)
    {
        var names = new List<string>();
        if (schema.ValueKind == JsonValueKind.Object
            && schema.TryGetProperty("properties", out var props)
            && props.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in props.EnumerateObject())
            {
                names.Add(prop.Name);
            }
        }
        return names;
    }

    /// <summary>GBNF literal matching the bare token <paramref name="text"/> (e.g. a tool name).</summary>
    private static string Literal(string text) => "\"" + EscapeForGbnf(text) + "\"";

    /// <summary>GBNF literal matching a JSON string token, i.e. the characters <c>"text"</c> including quotes.</summary>
    private static string JsonStringLiteral(string text) => "\"\\\"" + EscapeForGbnf(text) + "\\\"\"";

    /// <summary>Escapes backslashes and double-quotes for inclusion inside a GBNF double-quoted literal.</summary>
    private static string EscapeForGbnf(string text) =>
        text.Replace("\\", "\\\\").Replace("\"", "\\\"");
}
