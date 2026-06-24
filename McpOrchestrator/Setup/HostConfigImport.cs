using System.Text.Json;
using System.Text.Json.Nodes;
using McpOrchestrator.Orchestration;

namespace McpOrchestrator.Setup;

/// <summary>
/// What <see cref="HostConfigImport.Parse"/> extracts from an MCP host config: the parsed DOM
/// (so callers that rewrite it — like <c>init</c> — can mutate in place), the imported stdio
/// servers as capabilities, and what was deliberately left out (remote servers, a pre-existing
/// orchestrator entry). <c>profile --host-config</c> uses only <see cref="Capabilities"/> and
/// <see cref="SkippedRemote"/>; it writes nothing.
/// </summary>
internal sealed record HostImportResult(
    JsonObject Root,
    JsonObject Container,
    string ContainerKey,
    IReadOnlyList<CapabilityDescriptor> Capabilities,
    IReadOnlyList<string> Imported,
    IReadOnlyList<string> SkippedRemote,
    IReadOnlyList<string> OrchestratorKeys);

/// <summary>
/// Reads an existing MCP host config (<c>.mcp.json</c> / <c>.vscode/mcp.json</c> / Cursor /
/// Claude Desktop — both the <c>mcpServers</c> and <c>servers</c> shapes) and lifts every
/// <em>stdio</em> server into a <see cref="CapabilityDescriptor"/>. Pure (no IO): callers supply
/// the text. Shared by <c>init</c> (which then rewrites the host config) and <c>profile
/// --host-config</c> (which only measures, writing nothing).
/// </summary>
internal static class HostConfigImport
{
    /// <summary>
    /// Parses the host config text and classifies each server. Imports stdio servers as
    /// capabilities; records remote (http/sse/url) servers and command-less entries as skipped;
    /// records any pre-existing orchestrator entry separately so a rewriter can drop it.
    /// </summary>
    /// <exception cref="InvalidDataException">The root or server container isn't a JSON object, or neither container key is present.</exception>
    public static HostImportResult Parse(string hostConfigText)
    {
        var root = JsonNode.Parse(hostConfigText, documentOptions: new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        }) as JsonObject
            ?? throw new InvalidDataException("the host config root must be a JSON object.");

        // Claude Code / Cursor / Claude Desktop use "mcpServers"; VS Code / Visual Studio use "servers".
        var containerKey = root.ContainsKey("mcpServers") ? "mcpServers"
            : root.ContainsKey("servers") ? "servers"
            : throw new InvalidDataException(
                "no \"mcpServers\" or \"servers\" object found in the host config.");

        if (root[containerKey] is not JsonObject container)
        {
            throw new InvalidDataException($"the \"{containerKey}\" entry must be a JSON object.");
        }

        var capabilities = new List<CapabilityDescriptor>();
        var imported = new List<string>();
        var skippedRemote = new List<string>();
        var orchestratorKeys = new List<string>();

        foreach (var (name, value) in container)
        {
            if (value is not JsonObject server)
            {
                continue;
            }

            // A pre-existing orchestrator entry is not a downstream server. init drops and re-adds it
            // (so re-running is idempotent); profile ignores it.
            if (IsOrchestratorEntry(server))
            {
                orchestratorKeys.Add(name);
                continue;
            }

            var command = AsString(server["command"]);
            var isRemote = server["url"] is not null
                || string.Equals(AsString(server["type"]), "http", StringComparison.OrdinalIgnoreCase)
                || string.Equals(AsString(server["type"]), "sse", StringComparison.OrdinalIgnoreCase);

            if (isRemote || string.IsNullOrWhiteSpace(command))
            {
                // Remote servers (and anything without a launch command) can't be relayed over stdio.
                skippedRemote.Add(name);
                continue;
            }

            capabilities.Add(new CapabilityDescriptor
            {
                Name = name,
                // A visible TODO (rather than "") so it's obvious a human must fill this in when the
                // catalog is written by init. profile never reads the summary, so it's harmless there.
                Summary = $"TODO: Add one line on when the agent should use '{name}'.",
                Enabled = true,
                Transport = "stdio",
                Command = command,
                Args = AsStringList(server["args"]),
                WorkingDirectory = AsString(server["cwd"]) ?? AsString(server["workingDirectory"]),
                Env = AsStringDict(server["env"]),
            });
            imported.Add(name);
        }

        return new HostImportResult(root, container, containerKey, capabilities, imported, skippedRemote, orchestratorKeys);
    }

    /// <summary>True when a host-config entry is the orchestrator itself (so it's never imported or duplicated).</summary>
    private static bool IsOrchestratorEntry(JsonObject server)
    {
        var command = AsString(server["command"]);
        if (command is not null)
        {
            var leaf = Path.GetFileNameWithoutExtension(command);
            if (leaf.Contains("mcp-orchestrator", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (server["env"] is JsonObject env && env.ContainsKey("MCP_ORCHESTRATOR_CONFIG"))
        {
            return true;
        }

        return AsStringList(server["args"]).Any(a =>
            a.Contains("McpOrchestrator.csproj", StringComparison.OrdinalIgnoreCase));
    }

    // --- JsonNode readers (tolerant: coerce scalars to string, ignore the unexpected) ---

    internal static string? AsString(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue<string>(out var s) ? s : node?.ToString();

    internal static List<string> AsStringList(JsonNode? node) =>
        node is JsonArray arr
            ? arr.Where(a => a is not null).Select(a => AsString(a) ?? string.Empty).ToList()
            : new List<string>();

    internal static Dictionary<string, string?> AsStringDict(JsonNode? node)
    {
        var result = new Dictionary<string, string?>();
        if (node is JsonObject obj)
        {
            foreach (var (key, value) in obj)
            {
                result[key] = AsString(value);
            }
        }
        return result;
    }
}
