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

    [Fact]
    public void Compact_drops_text_wrapped_verbatim_in_a_structured_property()
    {
        // The filesystem server's shape: the file text, plus structuredContent {"content": <same text>}.
        const string file = "<Solution>\r\n  <Project Path=\"A.csproj\" />\r\n</Solution>\r\n";
        var (text, structured) = ToolPayloads.Compact(
            file, Json(JsonSerializer.Serialize(new { content = file, path = "A.slnx" })));

        Assert.Null(text);
        Assert.Equal(file, structured!.Value.GetProperty("content").GetString());
    }

    [Fact]
    public void Compact_drops_text_when_structured_content_is_that_string()
    {
        var (text, _) = ToolPayloads.Compact("[FILE] a.txt", Json("\"[FILE] a.txt\""));

        Assert.Null(text);
    }

    [Theory]
    [InlineData("""{"content":"[FILE] a.txt"}""")]          // only part of the text
    [InlineData("""{"result":{"content":"[FILE] a.txt\n[FILE] b.txt"}}""")] // nested, not top-level
    [InlineData("""{"lines":["[FILE] a.txt\n[FILE] b.txt"]}""")]              // inside an array
    public void Compact_keeps_text_the_structured_content_does_not_hold_at_top_level(string structuredJson)
    {
        const string listing = "[FILE] a.txt\n[FILE] b.txt";
        var (text, _) = ToolPayloads.Compact(listing, Json(structuredJson));

        Assert.Equal(listing, text);
    }

    [Fact]
    public void Compact_keeps_json_text_that_differs_from_structured_content()
    {
        const string rows = """[{"id":1},{"id":2},{"id":3}]""";
        var (text, structured) = ToolPayloads.Compact(rows, Json("""{"count":3}"""));

        Assert.Equal(rows, text);
        Assert.Equal(3, structured!.Value.GetProperty("count").GetInt32());
    }

    [Fact]
    public void Compact_treats_reformatted_copy_of_structured_content_as_duplicate()
    {
        var (text, _) = ToolPayloads.Compact("{ \"key\" : \"PROJ-1\" }", Json("""{"key":"PROJ-1"}"""));

        Assert.Null(text);
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

    // ----- TrimStderr -----

    [Fact]
    public void TrimStderr_keeps_the_cause_and_drops_frames_and_routine_log_entries()
    {
        // Captured from the demo server when get_issue is called with 'key' instead of 'issueKey'.
        string[] lines =
        [
            "info: ModelContextProtocol.Server.McpServer[570385771]",
            "      Server (McpOrchestrator.DemoMcp 1.0.0.0), Client (testhost 15.0.0.0) method 'tools/call' request handler called.",
            "fail: ModelContextProtocol.Server.McpServer[1433779783]",
            "      \"get_issue\" threw an unhandled exception.",
            "      System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'issueKey'. (Parameter 'arguments')",
            "         at Microsoft.Shared.Diagnostics.Throw.ArgumentException(String paramName, String message)",
            "         at ModelContextProtocol.Server.McpServerImpl.<InvokeHandlerAsync>g__InvokeScopedAsync|90_0[TParams,TResult](McpRequestHandler`2 handler, TParams args)",
            "      --- End of stack trace from previous location ---",
            "info: ModelContextProtocol.Server.McpServer[1867955179]",
            "      Server (McpOrchestrator.DemoMcp 1.0.0.0), Client (testhost 15.0.0.0) method 'tools/call' request handler completed in 2.2304ms.",
        ];

        var trimmed = ToolPayloads.TrimStderr(lines);

        Assert.Equal(
            [
                "fail: ModelContextProtocol.Server.McpServer[1433779783]",
                "      \"get_issue\" threw an unhandled exception.",
                "      System.ArgumentException: The arguments dictionary is missing a value for the required parameter 'issueKey'. (Parameter 'arguments')",
                "… (3 stack-frame, 4 info/debug log line(s) omitted) …",
            ],
            trimmed);
    }

    [Fact]
    public void TrimStderr_drops_an_exception_logged_twice()
    {
        string[] lines =
        [
            "fail: Server[1]",
            "      ModelContextProtocol.McpProtocolException: Unknown tool: 'get_ticket'",
            "warn: Server[2]",
            "      ModelContextProtocol.McpProtocolException: Unknown tool: 'get_ticket'",
        ];

        var trimmed = ToolPayloads.TrimStderr(lines);

        Assert.Equal(1, trimmed.Count(l => l.Contains("Unknown tool")));
        Assert.Contains("warn: Server[2]", trimmed);
        Assert.EndsWith("(1 repeated line(s) omitted) …", trimmed[^1]);
    }

    [Theory]
    [InlineData("    at Object.<anonymous> (/srv/index.js:12:5)")] // Node
    [InlineData("    at /srv/index.js:12:5")]                     // Node, anonymous
    [InlineData("\tat com.acme.Db.query(Db.java:42)")]              // Java
    [InlineData("   --- End of inner exception stack trace ---")]  // .NET
    public void TrimStderr_recognises_stack_frames_of_common_runtimes(string frame)
    {
        var trimmed = ToolPayloads.TrimStderr(["Error: boom", frame]);

        Assert.DoesNotContain(frame, trimmed);
        Assert.Contains("Error: boom", trimmed);
    }

    [Fact]
    public void TrimStderr_keeps_plain_output_and_prose_that_starts_with_at()
    {
        string[] lines = ["Traceback (most recent call last):", "  at least one of 'query' or 'id' is required", "ValueError: bad input"];

        Assert.Equal(lines, ToolPayloads.TrimStderr(lines));
    }

    [Fact]
    public void TrimStderr_returns_the_lines_unchanged_when_nothing_would_be_left()
    {
        string[] lines = ["info: Server[1]", "      Application started."];

        Assert.Equal(lines, ToolPayloads.TrimStderr(lines));
    }
}
