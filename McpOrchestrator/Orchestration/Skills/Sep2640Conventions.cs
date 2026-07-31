using System.Text.Json;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Every SEP-2640 convention in one place: the <c>skill://</c> URI scheme, the
/// <c>skill://index.json</c> catalog document, the extension identifier, and mime-type mapping.
/// </summary>
/// <remarks>
/// SEP-2640 (Skills Extension, modelcontextprotocol PR #2640) is a PENDING PROPOSAL, still in
/// draft as of July 2026. This class implements the published working-group draft from the
/// <c>modelcontextprotocol/experimental-ext-skills</c> repo: each skill file is an MCP resource
/// at <c>skill://&lt;name&gt;/&lt;file-path&gt;</c> and a well-known <c>skill://index.json</c>
/// resource carries the catalog (Agent Skills discovery format). The head of the PR has since
/// moved past that draft — it currently proposes dedicated <c>skills/list</c> /
/// <c>skills/get</c> methods with per-file digest manifests instead of the index resource — and
/// is still changing, so we deliberately stay on the published draft until the SEP merges. When
/// it does, this class (plus <see cref="SkillResourceHandlers"/>) is the whole diff.
/// </remarks>
internal static class Sep2640Conventions
{
    /// <summary>The extension identifier a server declares when it serves skills.</summary>
    internal const string ExtensionId = "io.modelcontextprotocol/skills";

    /// <summary>The URI of the well-known catalog resource.</summary>
    internal const string IndexUri = "skill://index.json";

    /// <summary>The discovery-format schema version the index declares.</summary>
    internal const string IndexSchema = "https://schemas.agentskills.io/discovery/0.2.0/schema.json";

    /// <summary>Builds the resource URI for one file of a skill.</summary>
    internal static string BuildUri(string skillName, string relativePath)
        => $"skill://{skillName}/{relativePath}";

    /// <summary>
    /// Splits a <c>skill://</c> URI into skill name and relative file path. The first path
    /// segment (RFC 3986 authority position) is the skill name per the draft's flat layout;
    /// it carries no host semantics.
    /// </summary>
    internal static bool TryParseUri(string uri, out string skillName, out string relativePath)
    {
        skillName = string.Empty;
        relativePath = string.Empty;
        const string prefix = "skill://";
        if (!uri.StartsWith(prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = uri[prefix.Length..];
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash <= 0 || slash == rest.Length - 1)
        {
            return false;
        }

        skillName = Uri.UnescapeDataString(rest[..slash]);
        relativePath = Uri.UnescapeDataString(rest[(slash + 1)..]);
        return true;
    }

    /// <summary>Serializes the catalog as the <c>skill://index.json</c> document.</summary>
    internal static string BuildIndexJson(SkillCatalog catalog)
    {
        var document = new SkillIndexDocument
        {
            Schema = IndexSchema,
            Skills = catalog.Skills.Select(s => new SkillIndexEntry
            {
                Name = s.Name,
                Type = "skill-md",
                Description = s.Description,
                Url = BuildUri(s.Name, SkillSnapshot.SkillFileName),
            }).ToList(),
        };

        return JsonSerializer.Serialize(document, SkillIndexJsonContext.Default.SkillIndexDocument);
    }

    /// <summary>
    /// Maps a skill file to a mime type: <c>text/markdown</c> for SKILL.md per the draft,
    /// otherwise a small extension table with <c>application/octet-stream</c> fallback.
    /// </summary>
    internal static string GetMimeType(string relativePath)
    {
        if (relativePath == SkillSnapshot.SkillFileName)
        {
            return "text/markdown";
        }

        return Path.GetExtension(relativePath).ToLowerInvariant() switch
        {
            ".md" => "text/markdown",
            ".txt" => "text/plain",
            ".json" => "application/json",
            ".yaml" or ".yml" => "application/yaml",
            ".py" => "text/x-python",
            ".sh" => "text/x-shellscript",
            ".js" or ".mjs" => "text/javascript",
            ".ts" => "text/typescript",
            ".cs" => "text/x-csharp",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".csv" => "text/csv",
            ".xml" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".pdf" => "application/pdf",
            _ => "application/octet-stream",
        };
    }

    /// <summary>True when the mime type is served inline as text rather than base64 blob.</summary>
    internal static bool IsTextMimeType(string mimeType)
        => mimeType.StartsWith("text/", StringComparison.Ordinal)
           || mimeType is "application/json" or "application/yaml" or "application/xml" or "image/svg+xml";
}
