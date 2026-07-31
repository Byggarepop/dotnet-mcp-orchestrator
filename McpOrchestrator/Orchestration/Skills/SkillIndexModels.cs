using System.Text.Json.Serialization;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// The Agent Skills discovery-index document (schemas.agentskills.io/discovery). Read by
/// <see cref="HttpSkillSource"/> to learn what a remote source offers, and written by
/// <see cref="Sep2640Conventions"/> as the <c>skill://index.json</c> catalog resource.
/// </summary>
public sealed class SkillIndexDocument
{
    /// <summary>The discovery-format schema URI; clients match it against known versions.</summary>
    [JsonPropertyName("$schema")]
    public string? Schema { get; set; }

    /// <summary>The catalog entries.</summary>
    public List<SkillIndexEntry> Skills { get; set; } = new();
}

/// <summary>One discovery-index entry.</summary>
public sealed class SkillIndexEntry
{
    /// <summary>Skill name; required for <c>skill-md</c> entries.</summary>
    public string? Name { get; set; }

    /// <summary>Entry kind; the orchestrator reads and writes <c>skill-md</c> entries only.</summary>
    public string? Type { get; set; }

    /// <summary>The skill description.</summary>
    public string? Description { get; set; }

    /// <summary>URL of the skill's SKILL.md (relative URLs resolve against the index URL).</summary>
    public string? Url { get; set; }

    /// <summary>
    /// Orchestrator extension for HTTP sources: supporting files (relative to the SKILL.md
    /// folder) to fetch alongside SKILL.md. Plain HTTP has no directory listing, so a source
    /// that wants <c>references/</c> etc. served must enumerate them here. Omitted when writing
    /// the SEP-2640 index — there, every file is already an enumerable resource.
    /// </summary>
    public List<string>? Files { get; set; }
}

/// <summary>Source-gen context for the discovery index (camelCase on write; AOT/trim-safe).</summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    AllowTrailingCommas = true,
    ReadCommentHandling = System.Text.Json.JsonCommentHandling.Skip,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true)]
[JsonSerializable(typeof(SkillIndexDocument))]
internal sealed partial class SkillIndexJsonContext : JsonSerializerContext;
