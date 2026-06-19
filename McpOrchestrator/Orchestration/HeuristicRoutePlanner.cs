using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ModelContextProtocol.Client;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// A dependency-free <see cref="IRoutePlanner"/> that picks a tool by keyword overlap
/// and fills arguments from the tool's input schema using simple extraction rules. It
/// is deliberately modest — enough to demo the <c>request</c> flow end-to-end without a
/// model. Swap it for an LLM-backed planner for production-quality routing.
/// </summary>
public sealed partial class HeuristicRoutePlanner : IRoutePlanner
{
    /// <summary>
    /// The minimal view of a downstream tool the planner reasons over: its name, optional
    /// description, and JSON input schema. Decouples the planning logic from
    /// <see cref="McpClientTool"/> so it can be exercised in isolation.
    /// </summary>
    internal readonly record struct ToolSpec(string Name, string? Description, JsonElement InputSchema);

    /// <inheritdoc />
    public Task<RoutePlan?> PlanAsync(
        string capability,
        IReadOnlyList<McpClientTool> tools,
        string request,
        CancellationToken cancellationToken)
    {
        var specs = tools
            .Select(t => new ToolSpec(t.Name, t.Description, t.ProtocolTool.InputSchema))
            .ToList();
        return Task.FromResult(Plan(specs, request));
    }

    /// <summary>
    /// Pure planning core: choose a tool by keyword overlap and fill its arguments. Returns
    /// <c>null</c> when there are no tools. Separated from <see cref="PlanAsync"/> so it can be
    /// unit-tested with synthetic <see cref="ToolSpec"/>s.
    /// </summary>
    internal static RoutePlan? Plan(IReadOnlyList<ToolSpec> tools, string request)
    {
        if (tools.Count == 0)
        {
            return null;
        }

        var requestWords = Tokenize(request);

        // Pick the tool whose name + description shares the most words with the request.
        // A single-tool capability is chosen unconditionally.
        var chosen = tools.Count == 1
            ? tools[0]
            : tools
                .Select(t => (tool: t, score: Score(t, requestWords)))
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.tool.Name, StringComparer.Ordinal)
                .First().tool;

        var (arguments, argNotes) = BuildArguments(chosen, request);

        var rationale =
            $"Heuristic: selected '{chosen.Name}' from {tools.Count} tool(s) by keyword overlap. {argNotes}";

        return new RoutePlan(chosen.Name, arguments, rationale);
    }

    /// <summary>Counts how many request words also appear in the tool's name and description.</summary>
    private static int Score(ToolSpec tool, IReadOnlySet<string> requestWords)
    {
        var haystack = Tokenize($"{tool.Name} {tool.Description}");
        return haystack.Count(requestWords.Contains);
    }

    /// <summary>
    /// Fills arguments from the tool's JSON input schema: an issue-key-like token
    /// (e.g. PROJ-123) goes to an id/key-style property; the remaining text goes to the
    /// first unfilled string property (required ones first).
    /// </summary>
    private static (IReadOnlyDictionary<string, object?> Arguments, string Notes) BuildArguments(
        ToolSpec tool, string request)
    {
        var args = new Dictionary<string, object?>();
        var properties = ReadStringProperties(tool.InputSchema, out var required);
        if (properties.Count == 0)
        {
            return (args, "Tool takes no (string) arguments; sent an empty argument set.");
        }

        var notes = new StringBuilder();

        // 1) Route an issue-key-like token to the most id/key-looking property.
        var key = IssueKeyRegex().Match(request);
        if (key.Success)
        {
            var target = properties.FirstOrDefault(p =>
                p.Contains("key", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("id", StringComparison.OrdinalIgnoreCase) ||
                p.Contains("issue", StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                args[target] = key.Value;
                notes.Append($"Mapped '{key.Value}' to '{target}'. ");
            }
        }

        // 2) Put the full request text into the first still-unfilled property,
        //    preferring a required one.
        var fallback = required.FirstOrDefault(p => !args.ContainsKey(p))
                       ?? properties.FirstOrDefault(p => !args.ContainsKey(p));
        if (fallback is not null)
        {
            args[fallback] = request;
            notes.Append($"Put the request text into '{fallback}'. ");
        }

        var unfilled = required.Where(p => !args.ContainsKey(p)).ToList();
        if (unfilled.Count > 0)
        {
            notes.Append($"Could not fill required arg(s): {string.Join(", ", unfilled)}.");
        }

        return (args, notes.ToString().TrimEnd());
    }

    /// <summary>Reads the names of string-typed properties (and which are required) from an input schema.</summary>
    private static List<string> ReadStringProperties(JsonElement schema, out List<string> required)
    {
        required = new List<string>();
        var names = new List<string>();

        if (schema.ValueKind == JsonValueKind.Object)
        {
            if (schema.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in props.EnumerateObject())
                {
                    var isString = !prop.Value.TryGetProperty("type", out var type)
                                   || (type.ValueKind == JsonValueKind.String && type.GetString() == "string");
                    if (isString)
                    {
                        names.Add(prop.Name);
                    }
                }
            }

            if (schema.TryGetProperty("required", out var req) && req.ValueKind == JsonValueKind.Array)
            {
                required = req.EnumerateArray()
                    .Where(e => e.ValueKind == JsonValueKind.String)
                    .Select(e => e.GetString()!)
                    .Where(names.Contains)
                    .ToList();
            }
        }

        return names;
    }

    /// <summary>Splits text into a lowercased set of words, dropping short tokens and stop-words.</summary>
    private static HashSet<string> Tokenize(string? text) =>
        WordRegex()
            .Matches(text ?? string.Empty)
            .Select(m => m.Value.ToLowerInvariant())
            .Where(w => w.Length > 2 && !StopWords.Contains(w))
            .ToHashSet();

    private static readonly HashSet<string> StopWords = new(StringComparer.Ordinal)
    {
        "the", "and", "for", "get", "from", "with", "that", "this", "you", "are", "was",
        "all", "any", "can", "use", "please", "give", "show", "tell", "what", "about",
    };

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9]*-\d+")]
    private static partial Regex IssueKeyRegex();

    [GeneratedRegex(@"[A-Za-z][A-Za-z0-9_]+")]
    private static partial Regex WordRegex();
}
