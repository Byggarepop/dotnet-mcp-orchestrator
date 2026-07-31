using System.Diagnostics.CodeAnalysis;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>The fields the orchestrator reads from a SKILL.md YAML frontmatter block.</summary>
/// <param name="Name">The required <c>name</c> field (validated per the Agent Skills spec).</param>
/// <param name="Description">The required <c>description</c> field (1–1024 chars).</param>
/// <param name="Body">The markdown body after the closing <c>---</c>, trimmed.</param>
internal sealed record SkillFrontmatter(string Name, string Description, string Body);

/// <summary>
/// Minimal, hand-rolled parser for SKILL.md frontmatter. Deliberately not a YAML library:
/// the Agent Skills spec only requires two scalar fields (<c>name</c>, <c>description</c>),
/// a full YAML dependency would cost AOT/trim friendliness, and skills whose frontmatter uses
/// YAML features this parser does not understand are simply skipped with a log line — never fatal.
/// </summary>
/// <remarks>
/// Supported subset: a leading <c>---</c> fence, <c>key: value</c> scalar lines (single- or
/// double-quoted values unquoted), <c>#</c> comment lines, and nested block values (which are
/// ignored — only top-level scalars are read). Unknown keys are ignored so optional spec fields
/// (<c>license</c>, <c>compatibility</c>, <c>metadata</c>, <c>allowed-tools</c>) pass through
/// harmlessly.
/// </remarks>
internal static class SkillFrontmatterParser
{
    /// <summary>Max length of the <c>name</c> field per the Agent Skills spec.</summary>
    internal const int MaxNameLength = 64;

    /// <summary>Max length of the <c>description</c> field per the Agent Skills spec.</summary>
    internal const int MaxDescriptionLength = 1024;

    /// <summary>
    /// Parses SKILL.md content. Returns false (with a human-readable reason for the log)
    /// when the frontmatter is missing, malformed, or fails Agent Skills validation.
    /// </summary>
    internal static bool TryParse(
        string content,
        [NotNullWhen(true)] out SkillFrontmatter? frontmatter,
        [NotNullWhen(false)] out string? error)
    {
        frontmatter = null;

        var lines = content.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd() != "---")
        {
            error = "missing leading '---' frontmatter fence";
            return false;
        }

        string? name = null;
        string? description = null;
        var bodyStart = -1;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.TrimEnd() == "---")
            {
                bodyStart = i + 1;
                break;
            }

            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            // Indented lines belong to a nested block value of a preceding key; only top-level
            // scalars are read, so they are skipped rather than rejected.
            if (line[0] is ' ' or '\t')
            {
                continue;
            }

            var colon = trimmed.IndexOf(':', StringComparison.Ordinal);
            if (colon <= 0)
            {
                error = $"malformed frontmatter line: '{Truncate(trimmed)}'";
                return false;
            }

            var key = trimmed[..colon].Trim();
            var value = Unquote(trimmed[(colon + 1)..].Trim());
            switch (key)
            {
                case "name":
                    name = value;
                    break;
                case "description":
                    description = value;
                    break;
            }
        }

        if (bodyStart < 0)
        {
            error = "missing closing '---' frontmatter fence";
            return false;
        }

        if (string.IsNullOrEmpty(name))
        {
            error = "frontmatter has no 'name'";
            return false;
        }

        if (!IsValidName(name))
        {
            error = $"invalid skill name '{Truncate(name)}' (must be 1-{MaxNameLength} lowercase alphanumerics/hyphens, no leading/trailing/double hyphen)";
            return false;
        }

        if (string.IsNullOrEmpty(description))
        {
            error = "frontmatter has no 'description'";
            return false;
        }

        if (description.Length > MaxDescriptionLength)
        {
            error = $"description exceeds {MaxDescriptionLength} characters";
            return false;
        }

        var body = string.Join('\n', lines[bodyStart..]).Trim();
        frontmatter = new SkillFrontmatter(name, description, body);
        error = null;
        return true;
    }

    /// <summary>Validates a skill name against the Agent Skills spec rules.</summary>
    internal static bool IsValidName(string name)
    {
        if (name.Length is 0 or > MaxNameLength)
        {
            return false;
        }

        if (name[0] == '-' || name[^1] == '-' || name.Contains("--", StringComparison.Ordinal))
        {
            return false;
        }

        foreach (var c in name)
        {
            if (c is not ((>= 'a' and <= 'z') or (>= '0' and <= '9') or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static string Unquote(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1];
        }

        return value;
    }

    private static string Truncate(string value)
        => value.Length <= 60 ? value : value[..60] + "…";
}
