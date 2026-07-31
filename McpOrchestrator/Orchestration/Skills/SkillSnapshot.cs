namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// An immutable, fully-materialized skill: frontmatter identity plus every file of the skill
/// folder read into memory. Serving always happens from a snapshot — never from the origin —
/// so path traversal is structurally impossible (lookups go through a dictionary, not the
/// filesystem), the integrity hash describes exactly what is served, and all source kinds
/// (directory, git, http) share one serving and hot-reload story.
/// </summary>
internal sealed class SkillSnapshot
{
    /// <summary>Path of the skill's entry file within the snapshot.</summary>
    internal const string SkillFileName = "SKILL.md";

    private readonly Dictionary<string, SkillFile> _files;

    internal SkillSnapshot(
        string name,
        string description,
        string body,
        string sourceId,
        IReadOnlyList<SkillFile> files)
    {
        Name = name;
        Description = description;
        Body = body;
        SourceId = sourceId;
        Files = files;
        _files = new Dictionary<string, SkillFile>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            _files[file.RelativePath] = file;
        }

        Sha256 = SkillHasher.ComputeHex(files);
    }

    /// <summary>Skill name from frontmatter (validated Agent Skills name).</summary>
    internal string Name { get; }

    /// <summary>Skill description from frontmatter.</summary>
    internal string Description { get; }

    /// <summary>The SKILL.md markdown body (frontmatter stripped).</summary>
    internal string Body { get; }

    /// <summary>Id of the config source this skill came from.</summary>
    internal string SourceId { get; }

    /// <summary>All files of the skill folder, SKILL.md included, relative paths with <c>/</c> separators.</summary>
    internal IReadOnlyList<SkillFile> Files { get; }

    /// <summary>Deterministic lowercase-hex SHA-256 over the snapshot content (see <see cref="SkillHasher"/>).</summary>
    internal string Sha256 { get; }

    /// <summary>
    /// Looks up a file by validated relative path. Returns false for anything not in the
    /// snapshot — including any path <see cref="SkillPathValidator"/> would reject, since
    /// such strings can never be dictionary keys.
    /// </summary>
    internal bool TryGetFile(string relativePath, out SkillFile file)
        => _files.TryGetValue(relativePath, out file!);
}

/// <summary>One file of a skill snapshot.</summary>
/// <param name="RelativePath">Path relative to the skill folder, normalized to <c>/</c> separators.</param>
/// <param name="Content">Raw file bytes.</param>
internal sealed record SkillFile(string RelativePath, byte[] Content);
