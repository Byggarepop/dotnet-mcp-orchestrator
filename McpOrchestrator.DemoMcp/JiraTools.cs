using System.ComponentModel;
using ModelContextProtocol.Server;

namespace McpOrchestrator.DemoMcp;

/// <summary>
/// Stand-in for a JIRA MCP server. Returns canned data so the orchestrator demo runs
/// with no external dependencies. Returning records (not strings) means the results
/// also flow back as MCP <em>structured content</em>.
/// </summary>
[McpServerToolType]
public sealed class JiraTools
{
    private static readonly Dictionary<string, Issue> Issues = new(StringComparer.OrdinalIgnoreCase)
    {
        ["PROJ-1"] = new("PROJ-1", "Orchestrator forwards calls to downstream MCPs", "In Progress", "sebastian"),
        ["PROJ-2"] = new("PROJ-2", "Add capability catalog config", "Done", "sebastian"),
        ["PROJ-3"] = new("PROJ-3", "Heuristic route planner for natural-language requests", "To Do", "unassigned"),
    };

    /// <summary>Returns the issue for <paramref name="issueKey"/>, or a placeholder if the key is unknown.</summary>
    [McpServerTool(Name = "get_issue")]
    [Description("Get a single JIRA issue by its key (e.g. PROJ-1). Returns key, summary, status, assignee.")]
    public static Issue GetIssue(
        [Description("The issue key, e.g. 'PROJ-1'.")] string issueKey)
        => Issues.TryGetValue(issueKey ?? string.Empty, out var issue)
            ? issue
            : new Issue(issueKey ?? "(none)", "Unknown issue (demo data only)", "Unknown", "unassigned");

    /// <summary>Returns issues whose summary contains <paramref name="query"/> (all issues if it is blank).</summary>
    [McpServerTool(Name = "search_issues")]
    [Description("Search JIRA issues by a free-text query against the summary. Returns matching issues.")]
    public static IReadOnlyList<Issue> SearchIssues(
        [Description("Text to match against issue summaries, e.g. 'planner'.")] string query)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return Issues.Values.ToList();
        }

        return Issues.Values
            .Where(i => i.Summary.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }
}

/// <summary>A simulated JIRA issue.</summary>
public sealed record Issue(string Key, string Summary, string Status, string Assignee);
