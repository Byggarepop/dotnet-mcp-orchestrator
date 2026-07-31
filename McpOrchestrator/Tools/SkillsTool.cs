using System.ComponentModel;
using System.Text;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Server;

namespace McpOrchestrator.Tools;

/// <summary>
/// The skills tool surface — delivery mode A (compatibility: works with every MCP client).
/// Follows the orchestrator's core principle: <c>list_skills</c> costs a name + one line per
/// skill, and full content loads only on demand via <c>get_skill</c> / <c>get_skill_file</c>,
/// mirroring the Agent Skills progressive-disclosure model. Every served body or file is
/// audit-logged with its content hash and source.
/// </summary>
[McpServerToolType]
public sealed class SkillsTool
{
    /// <summary>Cap for a <c>list_skills</c> description line, matching the capability summary budget.</summary>
    internal const int MaxListDescriptionChars = 150;

    /// <summary>
    /// Tool <c>list_skills</c>: the compact skill catalog — names and one-line descriptions only.
    /// </summary>
    [McpServerTool(Name = "list_skills")]
    [Description(
        "List the Agent Skills this orchestrator serves: procedural knowledge folders " +
        "(instructions, reference docs, scripts) that teach you how to perform specific tasks. " +
        "Each entry is a name plus a one-line description. When one matches your current task, " +
        "call 'get_skill' with its name to load the full instructions — do not guess content " +
        "from the description alone.")]
    public static Task<string> ListSkills(
        SkillRegistry registry,
        SkillsReloadService skills,
        ILogger<SkillsTool> logger)
    {
        if (!skills.Delivery.CatalogTools)
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView("skill catalog tools are disabled in this orchestrator's config")));
        }

        var catalog = registry.Current;
        logger.LogInformation("list_skills ({Count} available)", catalog.Skills.Count);
        var views = catalog.Skills
            .Select(s => new SkillListView(s.Name, TruncateAtWord(s.Description, MaxListDescriptionChars)))
            .ToList();
        return Task.FromResult(SkillJson.Serialize(views));
    }

    /// <summary>
    /// Tool <c>get_skill</c>: the full SKILL.md body plus the relative paths of the skill's
    /// supporting files.
    /// </summary>
    [McpServerTool(Name = "get_skill")]
    [Description(
        "Load one skill's full instructions (its SKILL.md body) plus the list of supporting " +
        "files it bundles (reference docs, scripts, assets). Follow the instructions; when they " +
        "reference a bundled file you need, fetch it with 'get_skill_file'. Load files on " +
        "demand rather than all up front.")]
    public static Task<string> GetSkill(
        SkillRegistry registry,
        SkillsReloadService skills,
        ILogger<SkillsTool> logger,
        [Description("Skill name from 'list_skills', e.g. 'pdf-processing'.")]
        string name)
    {
        if (!skills.Delivery.CatalogTools)
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView("skill catalog tools are disabled in this orchestrator's config")));
        }

        var catalog = registry.Current;
        if (!catalog.TryGet(name, out var skill))
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView(
                $"unknown skill '{name}'", catalog.Skills.Select(s => s.Name).ToList())));
        }

        // The audit trail: name + content hash + origin for every served body (Information level
        // so it lands in stderr and the rotating file log).
        logger.LogInformation(
            "skill served: skill={Skill} file={File} hash={Hash} source={Source} mode=tool",
            skill.Name, SkillSnapshot.SkillFileName, skill.Sha256, skill.SourceId);

        return Task.FromResult(SkillJson.Serialize(new SkillView(
            skill.Name,
            skill.Description,
            skill.Body,
            skill.Files.Select(f => f.RelativePath).Where(p => p != SkillSnapshot.SkillFileName).ToList(),
            skill.Sha256)));
    }

    /// <summary>
    /// Tool <c>get_skill_file</c>: one supporting file of a skill, by validated relative path.
    /// Text content is returned inline; binary content as base64.
    /// </summary>
    [McpServerTool(Name = "get_skill_file")]
    [Description(
        "Fetch one supporting file of a skill by its relative path (as listed by 'get_skill', " +
        "e.g. 'references/REFERENCE.md' or 'scripts/extract.py'). Text files are returned " +
        "inline; binary files as base64. Scripts are served as source for you to read or run " +
        "yourself — the orchestrator never executes them.")]
    public static Task<string> GetSkillFile(
        SkillRegistry registry,
        SkillsReloadService skills,
        ILogger<SkillsTool> logger,
        [Description("Skill name from 'list_skills'.")]
        string name,
        [Description("File path relative to the skill folder, e.g. 'references/REFERENCE.md'.")]
        string path)
    {
        if (!skills.Delivery.CatalogTools)
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView("skill catalog tools are disabled in this orchestrator's config")));
        }

        var catalog = registry.Current;
        if (!catalog.TryGet(name, out var skill))
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView(
                $"unknown skill '{name}'", catalog.Skills.Select(s => s.Name).ToList())));
        }

        if (!SkillPathValidator.TryNormalize(path, out var normalized))
        {
            logger.LogWarning("get_skill_file rejected path: skill={Skill} path={Path}", name, path);
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView(
                $"invalid file path '{path}': must be a plain relative path inside the skill folder")));
        }

        if (!skill.TryGetFile(normalized, out var file))
        {
            return Task.FromResult(SkillJson.Serialize(new SkillErrorView(
                $"skill '{skill.Name}' has no file '{normalized}'",
                skill.Files.Select(f => f.RelativePath).ToList())));
        }

        logger.LogInformation(
            "skill served: skill={Skill} file={File} hash={Hash} source={Source} mode=tool",
            skill.Name, normalized, skill.Sha256, skill.SourceId);

        var mimeType = Sep2640Conventions.GetMimeType(normalized);
        var view = Sep2640Conventions.IsTextMimeType(mimeType)
            ? new SkillFileView(skill.Name, normalized, mimeType, Encoding.UTF8.GetString(file.Content), null)
            : new SkillFileView(skill.Name, normalized, mimeType, null, Convert.ToBase64String(file.Content));
        return Task.FromResult(SkillJson.Serialize(view));
    }

    /// <summary>Collapses whitespace and truncates at a word boundary, ellipsis appended.</summary>
    internal static string TruncateAtWord(string text, int maxChars)
    {
        var collapsed = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (collapsed.Length <= maxChars)
        {
            return collapsed;
        }

        var cut = collapsed.LastIndexOf(' ', maxChars - 1);
        return collapsed[..(cut > 0 ? cut : maxChars - 1)] + "…";
    }
}
