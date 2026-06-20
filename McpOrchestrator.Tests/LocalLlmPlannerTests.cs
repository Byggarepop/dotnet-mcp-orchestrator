using System.Text.Json;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.LocalLlm;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests the deterministic parts of the local-LLM planner — grammar generation, the two-step
/// planning core (with a fake completer, no model), and the fallback decorator. None of these
/// download or run a model.
/// </summary>
public sealed class LocalLlmPlannerTests
{
    private static JsonElement Schema(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.Clone();
    }

    // ---------- GBNF grammar generation ----------

    [Fact]
    public void ToolNameChoice_lists_each_name_as_an_alternative()
    {
        var gbnf = GbnfGrammar.ToolNameChoice(new[] { "get_issue", "search_issues" });

        Assert.Contains("\"get_issue\"", gbnf);
        Assert.Contains("\"search_issues\"", gbnf);
        Assert.Contains("|", gbnf);
    }

    [Fact]
    public void JsonObjectWithKeys_restricts_keys_to_schema_properties()
    {
        var gbnf = GbnfGrammar.JsonObjectWithKeys(new[] { "issueKey", "limit" });

        // Keys appear as JSON-string literals: \"issueKey\"
        Assert.Contains("\\\"issueKey\\\"", gbnf);
        Assert.Contains("\\\"limit\\\"", gbnf);
        Assert.Contains("key ::=", gbnf);
        Assert.Contains("root ::=", gbnf);
    }

    [Fact]
    public void JsonObjectWithKeys_no_properties_matches_empty_object()
    {
        var gbnf = GbnfGrammar.JsonObjectWithKeys(Array.Empty<string>());
        Assert.Contains("root ::= \"{\" ws \"}\"", gbnf);
    }

    [Fact]
    public void PropertyNames_reads_top_level_properties()
    {
        var schema = Schema("""{ "type": "object", "properties": { "a": {"type":"string"}, "b": {"type":"number"} } }""");
        Assert.Equal(new[] { "a", "b" }, GbnfGrammar.PropertyNames(schema));
    }

    // ---------- Planner core with a fake completer ----------

    /// <summary>A completer that returns scripted answers and records the grammars it was given.</summary>
    private sealed class FakeCompleter : IConstrainedCompleter
    {
        private readonly Queue<string> _answers;
        public List<string> Grammars { get; } = new();

        public FakeCompleter(params string[] answers) => _answers = new Queue<string>(answers);

        public Task<string> CompleteAsync(string systemMessage, string userPrompt, string gbnf, CancellationToken ct)
        {
            Grammars.Add(gbnf);
            return Task.FromResult(_answers.Dequeue());
        }
    }

    private static LlmRoutePlanner.ToolSpec Tool(string name, string desc, string schema) =>
        new(name, desc, Schema(schema));

    private static LlmRoutePlanner Planner(IConstrainedCompleter completer) =>
        new(completer, new NullLogger<LlmRoutePlanner>(), "test-model");

    [Fact]
    public async Task PlanCore_no_tools_returns_null()
    {
        var plan = await Planner(new FakeCompleter())
            .PlanCoreAsync(Array.Empty<LlmRoutePlanner.ToolSpec>(), "anything", CancellationToken.None);
        Assert.Null(plan);
    }

    [Fact]
    public async Task PlanCore_single_tool_skips_selection_and_extracts_args()
    {
        // Only one tool -> no selection call; one completer call for argument extraction.
        var completer = new FakeCompleter("""{ "issueKey": "PROJ-1" }""");
        var tools = new[] { Tool("get_issue", "get an issue", """{ "type":"object","properties":{"issueKey":{"type":"string"}},"required":["issueKey"] }""") };

        var plan = await Planner(completer).PlanCoreAsync(tools, "status of PROJ-1", CancellationToken.None);

        Assert.Equal("get_issue", plan!.Tool);
        Assert.Equal("PROJ-1", ((JsonElement)plan.Arguments["issueKey"]!).GetString());
        Assert.Single(completer.Grammars); // only the argument-extraction call
    }

    [Fact]
    public async Task PlanCore_chooses_named_tool_then_extracts_args()
    {
        // First completer answer = tool name; second = JSON arguments.
        var completer = new FakeCompleter("generate_class", """{ "className": "Customer", "fields": "Id, Name" }""");
        var tools = new[]
        {
            Tool("get_issue", "get a jira issue", """{ "type":"object","properties":{"issueKey":{"type":"string"}} }"""),
            Tool("generate_class", "scaffold a class", """{ "type":"object","properties":{"className":{"type":"string"},"fields":{"type":"string"}},"required":["className"] }"""),
        };

        var plan = await Planner(completer).PlanCoreAsync(
            tools, "make a Customer class with Id and Name", CancellationToken.None);

        Assert.Equal("generate_class", plan!.Tool);
        Assert.Equal("Customer", ((JsonElement)plan.Arguments["className"]!).GetString());
        // The first grammar constrained tool selection to the real names.
        Assert.Contains("\"generate_class\"", completer.Grammars[0]);
    }

    [Fact]
    public async Task PlanCore_invalid_tool_answer_falls_back_to_first_tool()
    {
        var completer = new FakeCompleter("not_a_real_tool", "{}");
        var tools = new[]
        {
            Tool("aaa", "first", """{ "type":"object","properties":{} }"""),
            Tool("bbb", "second", """{ "type":"object","properties":{} }"""),
        };

        var plan = await Planner(completer).PlanCoreAsync(tools, "do something", CancellationToken.None);

        Assert.Equal("aaa", plan!.Tool);
    }

    [Fact]
    public async Task PlanCore_malformed_json_args_yield_empty_arguments()
    {
        var completer = new FakeCompleter("not valid json at all");
        var tools = new[] { Tool("only", "x", """{ "type":"object","properties":{"a":{"type":"string"}} }""") };

        var plan = await Planner(completer).PlanCoreAsync(tools, "x", CancellationToken.None);

        Assert.Empty(plan!.Arguments);
    }

    // ---------- Fallback decorator ----------

    private sealed class ThrowingPlanner : IRoutePlanner
    {
        public Task<RoutePlan?> PlanAsync(string c, IReadOnlyList<McpClientTool> t, string r, CancellationToken ct) =>
            throw new InvalidOperationException("model unavailable");
    }

    private sealed class FixedPlanner : IRoutePlanner
    {
        private readonly RoutePlan _plan;
        public bool Called { get; private set; }
        public FixedPlanner(RoutePlan plan) => _plan = plan;
        public Task<RoutePlan?> PlanAsync(string c, IReadOnlyList<McpClientTool> t, string r, CancellationToken ct)
        {
            Called = true;
            return Task.FromResult<RoutePlan?>(_plan);
        }
    }

    private static readonly RoutePlan FallbackPlan =
        new("fallback_tool", new Dictionary<string, object?>(), "heuristic");

    [Fact]
    public async Task Fallback_uses_secondary_when_primary_throws()
    {
        var fallback = new FixedPlanner(FallbackPlan);
        var planner = new FallbackRoutePlanner(new ThrowingPlanner(), fallback, new NullLogger<FallbackRoutePlanner>());

        var plan = await planner.PlanAsync("cap", Array.Empty<McpClientTool>(), "req", CancellationToken.None);

        Assert.True(fallback.Called);
        Assert.Equal("fallback_tool", plan!.Tool);
    }

    [Fact]
    public async Task Fallback_uses_primary_result_when_it_succeeds()
    {
        var primaryPlan = new RoutePlan("primary_tool", new Dictionary<string, object?>(), "llm");
        var fallback = new FixedPlanner(FallbackPlan);
        var planner = new FallbackRoutePlanner(new FixedPlanner(primaryPlan), fallback, new NullLogger<FallbackRoutePlanner>());

        var plan = await planner.PlanAsync("cap", Array.Empty<McpClientTool>(), "req", CancellationToken.None);

        Assert.Equal("primary_tool", plan!.Tool);
        Assert.False(fallback.Called);
    }
}
