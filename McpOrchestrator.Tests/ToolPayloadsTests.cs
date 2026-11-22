using System.Text.Json;
using McpOrchestrator.Orchestration;
using ModelContextProtocol.Protocol;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>Unit tests for the pure argument/result conversions in <see cref="ToolPayloads"/>.</summary>
public sealed class ToolPayloadsTests
{
    private static JsonElement Json(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    [Fact]
    public void ParseArguments_object_keeps_all_properties()
    {
        var args = ToolPayloads.ParseArguments(Json("""{ "issueKey": "PROJ-1", "limit": 5 }"""));

        Assert.Equal(2, args.Count);
        Assert.Equal("PROJ-1", ((JsonElement)args["issueKey"]!).GetString());
        Assert.Equal(5, ((JsonElement)args["limit"]!).GetInt32());
    }

    [Fact]
    public void ParseArguments_accepts_object_passed_as_json_string()
    {
        // Some hosts stringify the arguments object; the parser should unwrap it.
        var args = ToolPayloads.ParseArguments(Json("""  "{ \"className\": \"Customer\" }"  """.Trim()));

        Assert.Single(args);
        Assert.Equal("Customer", ((JsonElement)args["className"]!).GetString());
    }

    [Fact]
    public void ParseArguments_non_json_string_yields_empty()
    {
        var args = ToolPayloads.ParseArguments(Json("\"just some text\""));
        Assert.Empty(args);
    }

    [Fact]
    public void ParseArguments_scalar_array_and_null_yield_empty()
    {
        Assert.Empty(ToolPayloads.ParseArguments(Json("42")));
        Assert.Empty(ToolPayloads.ParseArguments(Json("[1, 2, 3]")));
        Assert.Empty(ToolPayloads.ParseArguments(Json("null")));
    }

    [Fact]
    public void ParseArguments_undefined_element_yields_empty()
    {
        // default(JsonElement) has ValueKind.Undefined — what a host passes when 'arguments' is omitted.
        Assert.Empty(ToolPayloads.ParseArguments(default));
    }

    [Fact]
    public void FlattenText_joins_text_blocks_with_newlines()
    {
        var result = new CallToolResult
        {
            Content = new List<ContentBlock>
            {
                new TextContentBlock { Text = "first" },
                new TextContentBlock { Text = "second" },
            },
        };

        Assert.Equal("first\nsecond", ToolPayloads.FlattenText(result));
    }

    [Fact]
    public void FlattenText_empty_content_is_empty_string()
    {
        var result = new CallToolResult { Content = new List<ContentBlock>() };
        Assert.Equal(string.Empty, ToolPayloads.FlattenText(result));
    }

    // ----- Compact -----

    [Fact]
    public void Compact_drops_text_that_repeats_structured_content()
    {
        var (text, structured) = ToolPayloads.Compact("""{"key":"PROJ-1"}""", Json("""{"key":"PROJ-1"}"""));

        Assert.Null(text);
        Assert.Equal("PROJ-1", structured!.Value.GetProperty("key").GetString());
    }

    [Fact]
    public void Compact_keeps_plain_text_beside_structured_content()
    {
        var (text, structured) = ToolPayloads.Compact("Found 1 issue.", Json("""{"key":"PROJ-1"}"""));

        Assert.Equal("Found 1 issue.", text);
        Assert.NotNull(structured);
    }

    [Theory]
    [InlineData("""{"key":"PROJ-1"}""", JsonValueKind.Object)]
    [InlineData("""  [{"id":1},{"id":2}]""", JsonValueKind.Array)]
    public void Compact_embeds_json_text_as_json(string raw, JsonValueKind kind)
    {
        var (text, structured) = ToolPayloads.Compact(raw, null);

        Assert.Null(text);
        Assert.Equal(kind, structured!.Value.ValueKind);
    }

    [Theory]
    [InlineData("public sealed class Customer { }")]
    [InlineData("{ not json")]
    [InlineData("{\"a\":1}\n{\"b\":2}")] // two text blocks joined by a newline
    [InlineData("42")]                        // a bare JSON scalar stays text
    public void Compact_keeps_non_json_text_as_text(string raw)
    {
        var (text, structured) = ToolPayloads.Compact(raw, null);

        Assert.Equal(raw, text);
        Assert.Null(structured);
    }

    [Fact]
    public void Compact_turns_empty_text_into_no_text()
    {
        var (text, structured) = ToolPayloads.Compact(string.Empty, null);

        Assert.Null(text);
        Assert.Null(structured);
    }
}
