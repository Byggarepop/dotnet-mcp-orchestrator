using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>Serializes the structured strings the skills tools return to the agent.</summary>
internal static class SkillJson
{
    /// <summary>Source-generated serialization (Native-AOT safe); mirrors <see cref="OrchestratorJson"/>.</summary>
    internal static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, typeof(T), SkillViewsJsonContext.Default);
}

/// <summary>
/// Source-generation context for the skills tool responses. Same shape conventions as
/// <see cref="OrchestratorJsonContext"/>: indented, camelCase, null-skipping.
/// </summary>
[JsonSourceGenerationOptions(
    WriteIndented = true,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(List<SkillListView>))]
[JsonSerializable(typeof(SkillView))]
[JsonSerializable(typeof(SkillFileView))]
[JsonSerializable(typeof(SkillErrorView))]
internal sealed partial class SkillViewsJsonContext : JsonSerializerContext;

/// <summary>One catalog line of <c>list_skills</c>: the minimal scent, full detail on demand.</summary>
public sealed record SkillListView(string Name, string Description);

/// <summary>
/// The result of <c>get_skill</c>: the SKILL.md body plus the supporting files the agent can
/// fetch with <c>get_skill_file</c>.
/// </summary>
public sealed record SkillView(
    string Name,
    string Description,
    string Body,
    IReadOnlyList<string> Files,
    string Sha256);

/// <summary>
/// The result of <c>get_skill_file</c>. Text files arrive in <see cref="Text"/>; binary files
/// in <see cref="Base64"/> — exactly one is set.
/// </summary>
public sealed record SkillFileView(
    string Skill,
    string Path,
    string MimeType,
    string? Text,
    string? Base64);

/// <summary>A structured error returned to the model instead of throwing.</summary>
public sealed record SkillErrorView(string Error, IReadOnlyList<string>? AvailableSkills = null);
