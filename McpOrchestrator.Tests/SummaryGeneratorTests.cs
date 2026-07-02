using McpOrchestrator.Setup;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers the pure summary derivation in <see cref="SummaryGenerator"/> — the fallback chain
/// <c>init</c> uses to auto-fill each capability's "summary" — plus how <see cref="InitCommand.Plan"/>
/// weaves generated summaries (and their auto-generated markers) into the catalog text. No server
/// connections here; the connected path is covered by the init integration tests.
/// </summary>
public sealed class SummaryGeneratorTests
{
    // ----- instructions → first sentence -------------------------------------------------------

    [Fact]
    public void FromInstructions_takes_the_first_sentence_only()
    {
        var summary = SummaryGenerator.FromInstructions(
            "Use this server to manage Jira issues. It also supports boards, sprints and users.");

        Assert.Equal("Use this server to manage Jira issues.", summary);
    }

    [Fact]
    public void FromInstructions_collapses_newlines_and_does_not_cut_inside_dotted_tokens()
    {
        var summary = SummaryGenerator.FromInstructions(
            "Query the v2.0 API\nfor customer records!\nSecond sentence.");

        Assert.Equal("Query the v2.0 API for customer records!", summary);
    }

    [Fact]
    public void FromInstructions_truncates_a_long_sentence_at_150_chars_on_a_word_boundary()
    {
        var instructions = string.Join(' ', Enumerable.Repeat("lorem ipsum dolor sit amet", 10)) + ".";

        var summary = SummaryGenerator.FromInstructions(instructions);

        Assert.NotNull(summary);
        Assert.True(summary!.Length <= 150, $"expected <=150 chars, got {summary.Length}");
        Assert.EndsWith("…", summary);
        // The kept text is a clean prefix ending exactly where a word ends in the source.
        var kept = summary[..^1];
        Assert.StartsWith(kept, instructions);
        Assert.Equal(' ', instructions[kept.Length]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   \n  ")]
    public void FromInstructions_returns_null_when_there_is_nothing_to_summarize(string? instructions)
    {
        Assert.Null(SummaryGenerator.FromInstructions(instructions));
    }

    // ----- tool-name template fallback ----------------------------------------------------------

    [Fact]
    public void FromToolNames_with_seven_tools_lists_five_and_appends_an_ellipsis()
    {
        var summary = SummaryGenerator.FromToolNames(
            "acme-server", new[] { "t1", "t2", "t3", "t4", "t5", "t6", "t7" });

        Assert.Equal("7 tools for acme-server: t1, t2, t3, t4, t5, …", summary);
    }

    [Fact]
    public void FromToolNames_with_three_tools_lists_all_without_an_ellipsis()
    {
        var summary = SummaryGenerator.FromToolNames("acme-server", new[] { "t1", "t2", "t3" });

        Assert.Equal("3 tools for acme-server: t1, t2, t3", summary);
    }

    [Fact]
    public void FromToolNames_caps_the_template_at_150_chars()
    {
        var names = Enumerable.Range(1, 5).Select(i => new string((char)('a' + i), 60)).ToList();

        var summary = SummaryGenerator.FromToolNames("srv", names);

        Assert.NotNull(summary);
        Assert.True(summary!.Length <= 150, $"expected <=150 chars, got {summary.Length}");
        Assert.EndsWith("…", summary);
    }

    [Fact]
    public void FromToolNames_returns_null_when_the_server_has_no_tools()
    {
        Assert.Null(SummaryGenerator.FromToolNames("srv", Array.Empty<string>()));
    }

    // ----- Plan weaving the summaries into the catalog ------------------------------------------

    private const string TwoServers = """
        {
          "mcpServers": {
            "files": { "command": "npx", "args": ["server-filesystem"] },
            "jira":  { "command": "dotnet", "args": ["Jira.dll"] }
          }
        }
        """;

    [Fact]
    public void Plan_applies_generated_summaries_and_marks_only_those_lines_as_auto_generated()
    {
        var summaries = new Dictionary<string, string> { ["jira"] = "2 tools for jira-server: get_issue, search_issues" };

        var plan = InitCommand.Plan(TwoServers, ".mcp.json", "/out/cfg.json", "mcp-orchestrator", summaries: summaries);

        Assert.Equal(new[] { "jira" }, plan.AutoSummarized);

        var lines = plan.OrchestratorConfigText.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var jiraSummary = lines.Single(l => l.Contains("get_issue"));
        Assert.EndsWith(InitCommand.AutoSummaryComment, jiraSummary);
        var filesSummary = lines.Single(l => l.Contains("\"summary\"") && l.Contains("TODO"));
        Assert.DoesNotContain("auto-generated", filesSummary);

        // The trailing comments must not break the config reader.
        var catalog = System.Text.Json.JsonSerializer.Deserialize(
            plan.OrchestratorConfigText, Orchestration.OrchestratorConfigJsonContext.Default.OrchestratorConfig)!;
        Assert.Equal("2 tools for jira-server: get_issue, search_issues", catalog.Capabilities.Single(c => c.Name == "jira").Summary);
        Assert.Contains("TODO", catalog.Capabilities.Single(c => c.Name == "files").Summary);
    }

    [Fact]
    public void Plan_without_summaries_keeps_todo_placeholders_and_no_markers()
    {
        var plan = InitCommand.Plan(TwoServers, ".mcp.json", "/out/cfg.json", "mcp-orchestrator");

        Assert.Empty(plan.AutoSummarized);
        Assert.DoesNotContain("auto-generated", plan.OrchestratorConfigText);
    }
}
