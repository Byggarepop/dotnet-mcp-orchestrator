using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Turns a filesystem tree into skill snapshots: finds every <c>SKILL.md</c> recursively,
/// parses its frontmatter, and materializes the surrounding folder. Shared by the
/// <c>directory</c> source and the <c>git</c> source (which scans its local clone).
/// Invalid skills are logged and skipped — discovery is never fatal.
/// </summary>
internal static class SkillDirectoryScanner
{
    /// <summary>Per-file size cap; larger files are skipped with a warning (matches the central-config payload cap).</summary>
    internal const long MaxFileBytes = 1024 * 1024;

    /// <summary>Per-skill file-count cap, bounding snapshot memory for a pathological folder.</summary>
    internal const int MaxFilesPerSkill = 200;

    /// <summary>Scans <paramref name="root"/> for skills belonging to <paramref name="sourceId"/>.</summary>
    internal static IReadOnlyList<SkillSnapshot> Scan(string root, string sourceId, ILogger logger)
    {
        var skills = new List<SkillSnapshot>();
        if (!Directory.Exists(root))
        {
            logger.LogWarning("skill source {Source}: directory {Root} does not exist; no skills loaded", sourceId, root);
            return skills;
        }

        var fullRoot = Path.GetFullPath(root);
        var skillFiles = Directory
            .EnumerateFiles(fullRoot, SkillSnapshot.SkillFileName, SearchOption.AllDirectories)
            .Where(p => !p.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains(".git", StringComparer.OrdinalIgnoreCase))
            .Select(p => Path.GetDirectoryName(p)!)
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        // Skills do not nest (SEP-2640): a SKILL.md above another one wins and the nested folder
        // is skipped, so the outer skill's snapshot unambiguously owns its whole subtree.
        var accepted = new List<string>();
        foreach (var dir in skillFiles)
        {
            var parent = accepted.FirstOrDefault(a =>
                dir.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase));
            if (parent is not null)
            {
                logger.LogWarning(
                    "skill source {Source}: skipping nested skill at {Dir} (inside the skill at {Parent}; skills do not nest)",
                    sourceId, dir, parent);
                continue;
            }

            accepted.Add(dir);
            if (TryLoadSkillFolder(dir, sourceId, logger, out var skill))
            {
                skills.Add(skill);
            }
        }

        return skills;
    }

    /// <summary>Materializes a single skill folder into a snapshot; false (with a log line) when invalid.</summary>
    internal static bool TryLoadSkillFolder(string dir, string sourceId, ILogger logger, out SkillSnapshot skill)
    {
        skill = null!;
        string content;
        try
        {
            content = File.ReadAllText(Path.Combine(dir, SkillSnapshot.SkillFileName));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogWarning(ex, "skill source {Source}: failed to read SKILL.md in {Dir}; skipped", sourceId, dir);
            return false;
        }

        if (!SkillFrontmatterParser.TryParse(content, out var frontmatter, out var error))
        {
            logger.LogWarning("skill source {Source}: invalid skill at {Dir}: {Error}; skipped", sourceId, dir, error);
            return false;
        }

        // The Agent Skills spec requires the name to match the parent directory name.
        var dirName = Path.GetFileName(dir);
        if (!string.Equals(dirName, frontmatter.Name, StringComparison.Ordinal))
        {
            logger.LogWarning(
                "skill source {Source}: skill '{Name}' at {Dir} skipped: frontmatter name must match its folder name ('{DirName}')",
                sourceId, frontmatter.Name, dir, dirName);
            return false;
        }

        var files = new List<SkillFile>();
        foreach (var path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                     .OrderBy(p => p, StringComparer.Ordinal))
        {
            var relative = Path.GetRelativePath(dir, path).Replace('\\', '/');
            var info = new FileInfo(path);
            if (info.Length > MaxFileBytes)
            {
                logger.LogWarning(
                    "skill source {Source}: skill '{Name}' file {File} exceeds {Max} bytes; file skipped",
                    sourceId, frontmatter.Name, relative, MaxFileBytes);
                continue;
            }

            if (files.Count >= MaxFilesPerSkill)
            {
                logger.LogWarning(
                    "skill source {Source}: skill '{Name}' has more than {Max} files; the rest are skipped",
                    sourceId, frontmatter.Name, MaxFilesPerSkill);
                break;
            }

            try
            {
                files.Add(new SkillFile(relative, File.ReadAllBytes(path)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "skill source {Source}: failed to read {File}; file skipped", sourceId, relative);
            }
        }

        skill = new SkillSnapshot(frontmatter.Name, frontmatter.Description, frontmatter.Body, sourceId, files);
        return true;
    }
}
