using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// The value of a capability's <c>enabled</c> field before placeholder resolution: either a JSON
/// boolean (<c>true</c>/<c>false</c>, the only form prior versions accepted) or a string holding
/// a <c>${VAR}</c> placeholder such as <c>"${USE_DATABASE:-false}"</c>, so a centrally served
/// catalog can let each consuming machine switch a capability on or off through its environment.
/// A bare <c>"true"</c>/<c>"false"</c> string is accepted as the boolean it spells.
/// </summary>
[JsonConverter(typeof(EnabledSettingJsonConverter))]
public sealed record EnabledSetting
{
    public static readonly EnabledSetting True = new(true, null);
    public static readonly EnabledSetting False = new(false, null);

    private EnabledSetting(bool? literal, string? expression)
    {
        Literal = literal;
        Expression = expression;
    }

    /// <summary>The boolean value when the field is literal (or already resolved); <c>null</c> while it is still an expression.</summary>
    public bool? Literal { get; }

    /// <summary>The raw <c>${VAR}</c> text when the field is an expression; <c>null</c> when literal.</summary>
    public string? Expression { get; }

    /// <summary><c>true</c> when the field still holds an unresolved <c>${VAR}</c> expression.</summary>
    public bool IsExpression => Expression is not null;

    public static implicit operator EnabledSetting(bool value) => value ? True : False;

    /// <summary>Wraps a <c>${VAR}</c> expression (the caller has checked it contains a placeholder).</summary>
    public static EnabledSetting FromExpression(string expression) => new(null, expression);

    /// <summary>
    /// Parses a string form of the field: the boolean words below map to a literal, a string
    /// containing <c>${</c> becomes an expression, anything else is <c>null</c> (invalid).
    /// </summary>
    public static EnabledSetting? Parse(string text)
    {
        if (TryParseBoolean(text, out var value))
        {
            return value ? True : False;
        }

        return text.Contains("${", StringComparison.Ordinal) ? FromExpression(text) : null;
    }

    /// <summary>Accepts true/false, yes/no, on/off, 1/0 (case-insensitive, trimmed).</summary>
    public static bool TryParseBoolean(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "on" or "1":
                value = true;
                return true;
            case "false" or "no" or "off" or "0":
                value = false;
                return true;
            default:
                value = false;
                return false;
        }
    }

    public override string ToString() => Expression ?? (Literal == true ? "true" : "false");
}

/// <summary>
/// Reads <c>enabled</c> as a JSON boolean or a string (see <see cref="EnabledSetting"/>); writes a
/// literal back as a boolean and an expression back as its string, so a round-tripped catalog keeps
/// the author's form.
/// </summary>
public sealed class EnabledSettingJsonConverter : JsonConverter<EnabledSetting>
{
    public override EnabledSetting Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        switch (reader.TokenType)
        {
            case JsonTokenType.True:
                return EnabledSetting.True;
            case JsonTokenType.False:
                return EnabledSetting.False;
            case JsonTokenType.String:
                var text = reader.GetString() ?? string.Empty;
                return EnabledSetting.Parse(text)
                    ?? throw new JsonException(
                        $"'enabled' must be true, false, or a ${{VAR}} placeholder string; got \"{text}\".");
            default:
                throw new JsonException(
                    $"'enabled' must be true, false, or a ${{VAR}} placeholder string; got a JSON {reader.TokenType}.");
        }
    }

    public override void Write(Utf8JsonWriter writer, EnabledSetting value, JsonSerializerOptions options)
    {
        if (value.Expression is not null)
        {
            writer.WriteStringValue(value.Expression);
        }
        else
        {
            writer.WriteBooleanValue(value.Literal ?? true);
        }
    }
}
