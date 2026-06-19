using System.Text.Json;
using McpOrchestrator.Orchestration;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests the pure planning core of <see cref="HeuristicRoutePlanner"/>. These also serve as
/// executable documentation of the heuristic's deliberate limits — including the case where
/// it mis-maps a whole sentence into a single argument (why <c>route</c> is preferred over
/// <c>request</c>).
/// </summary>
public sealed class HeuristicRoutePlannerTests
{
    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    private static HeuristicRoutePlanner.ToolSpec Tool(string name, string? description, string schemaJson) =>
        new(name, description, Schema(schemaJson));

    private const string OneRequiredString = """
        { "type": "object", "properties": { "value": { "type": "string" } }, "required": ["value"] }
        """;

    [Fact]
    public void Plan_no_tools_returns_null()
    {
        Assert.Null(HeuristicRoutePlanner.Plan(Array.Empty<HeuristicRoutePlanner.ToolSpec>(), "anything"));
    }

    [Fact]
    public void Plan_single_tool_is_chosen_unconditionally()
    {
        var plan = HeuristicRoutePlanner.Plan(
            new[] { Tool("only_tool", "does a thing", OneRequiredString) },
            "completely unrelated wording");

        Assert.NotNull(plan);
        Assert.Equal("only_tool", plan!.Tool);
    }

    [Fact]
    public void Plan_picks_tool_with_most_keyword_overlap()
    {
        var tools = new[]
        {
            Tool("get_issue", "get a jira issue by key", OneRequiredString),
            Tool("generate_class", "scaffold a csharp class", OneRequiredString),
        };

        var plan = HeuristicRoutePlanner.Plan(tools, "scaffold a class please");

        Assert.Equal("generate_class", plan!.Tool);
    }

    [Fact]
    public void Plan_maps_issue_key_to_key_like_property()
    {
        var schema = """
            { "type": "object", "properties": { "issueKey": { "type": "string" } }, "required": ["issueKey"] }
            """;
        var plan = HeuristicRoutePlanner.Plan(
            new[] { Tool("get_issue", "get an issue", schema) },
            "status of PROJ-42 please");

        Assert.Equal("PROJ-42", (string?)plan!.Arguments["issueKey"]);
    }

    [Fact]
    public void Plan_no_argument_tool_sends_empty_arguments()
    {
        var plan = HeuristicRoutePlanner.Plan(
            new[] { Tool("ping", "no args", """{ "type": "object", "properties": {} }""") },
            "ping it");

        Assert.Empty(plan!.Arguments);
    }

    [Fact]
    public void Plan_tie_breaks_on_ordinal_name()
    {
        // Two tools with zero keyword overlap -> tie -> alphabetical (Ordinal) wins: "aaa" < "bbb".
        var tools = new[]
        {
            Tool("bbb", "zzz", OneRequiredString),
            Tool("aaa", "zzz", OneRequiredString),
        };

        var plan = HeuristicRoutePlanner.Plan(tools, "qqq qqq");

        Assert.Equal("aaa", plan!.Tool);
    }

    [Fact]
    public void Plan_known_weakness_dumps_whole_sentence_into_lone_string_arg()
    {
        // Documents why 'request' is best-effort: with a single free-text property and no
        // issue-key token, the planner puts the ENTIRE request into that property.
        var schema = """
            { "type": "object", "properties": { "className": { "type": "string" } }, "required": ["className"] }
            """;
        var request = "generate a class named Customer with fields Id, Name, Email";

        var plan = HeuristicRoutePlanner.Plan(new[] { Tool("generate_class", "scaffold", schema) }, request);

        Assert.Equal(request, (string?)plan!.Arguments["className"]);
    }
}
