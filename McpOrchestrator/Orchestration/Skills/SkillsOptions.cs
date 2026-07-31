namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// The <c>skills</c> section of the orchestrator config: where Agent Skills (SKILL.md folders)
/// come from, how they are delivered over MCP, and the governance rules applied before serving.
/// </summary>
/// <remarks>
/// The skill folder format follows the Agent Skills specification (agentskills.io): a directory
/// with a <c>SKILL.md</c> whose YAML frontmatter carries <c>name</c> and <c>description</c>, plus
/// optional <c>scripts/</c>, <c>references/</c>, and <c>assets/</c> folders. The orchestrator only
/// serves skill files — it never executes anything under <c>scripts/</c>.
/// </remarks>
public sealed class SkillsOptions
{
    /// <summary>Master switch. When false the skills subsystem registers nothing.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>The places skills are loaded from, in priority order (first wins on name collision).</summary>
    public List<SkillSourceOptions> Sources { get; set; } = new();

    /// <summary>How skills are exposed to MCP clients.</summary>
    public SkillDeliveryOptions Delivery { get; set; } = new();

    /// <summary>Allow/deny lists and integrity pinning.</summary>
    public SkillGovernanceOptions Governance { get; set; } = new();
}

/// <summary>
/// One skill source. <see cref="Type"/> selects the kind; the other properties are
/// kind-specific and ignored when they do not apply.
/// </summary>
public sealed class SkillSourceOptions
{
    /// <summary>
    /// Stable identifier for this source, used in audit logs and cache folder names.
    /// Required; must be unique within the config.
    /// </summary>
    public string Id { get; set; } = string.Empty;

    /// <summary>Source kind: <c>directory</c>, <c>git</c>, or <c>http</c>.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>
    /// (<c>directory</c>) Root folder scanned recursively for <c>SKILL.md</c> files.
    /// Supports <c>${VAR}</c> substitution.
    /// </summary>
    public string? Path { get; set; }

    /// <summary>(<c>git</c>) Clone URL, https. Supports <c>${VAR}</c> substitution.</summary>
    public string? Url { get; set; }

    /// <summary>(<c>git</c>) Branch, tag, or commit to check out. Defaults to the remote default branch.</summary>
    public string? Ref { get; set; }

    /// <summary>
    /// (<c>git</c>) Bearer token for private repositories, passed as an
    /// <c>Authorization: Bearer</c> header via git's <c>http.extraHeader</c>. Supports
    /// <c>${VAR}</c> substitution. Never logged.
    /// </summary>
    public string? Token { get; set; }

    /// <summary>
    /// (<c>http</c>) URL of a discovery index (agentskills.io discovery format) listing the
    /// skills and their files; file URLs resolve relative to this URL. Supports <c>${VAR}</c>.
    /// </summary>
    public string? IndexUrl { get; set; }

    /// <summary>
    /// (<c>http</c>) Verbatim <c>Authorization</c> header value for the index and file requests.
    /// Supports <c>${VAR}</c> substitution. Never logged.
    /// </summary>
    public string? Authorization { get; set; }

    /// <summary>
    /// (<c>git</c>/<c>http</c>) Seconds between refresh checks. Default 300, minimum 10 —
    /// same policy as central config polling. Ignored for <c>directory</c> sources, which use
    /// a file watcher instead.
    /// </summary>
    public int? PollSeconds { get; set; }
}

/// <summary>Which MCP surfaces expose the skill catalog.</summary>
public sealed class SkillDeliveryOptions
{
    /// <summary>
    /// Expose the compact tool trio: <c>list_skills</c> (names + one-line descriptions),
    /// <c>get_skill</c> (SKILL.md body), <c>get_skill_file</c> (supporting file). Default on —
    /// this is the compatibility mode that works with every MCP client.
    /// </summary>
    public bool CatalogTools { get; set; } = true;

    /// <summary>
    /// Additionally expose one <c>skill_&lt;name&gt;</c> tool per skill. Default off: per-skill
    /// tools inflate every session's context, which is the opposite of what this orchestrator
    /// is for. The catalog trio serves the same content on demand.
    /// </summary>
    public bool PerSkillTools { get; set; }

    /// <summary>
    /// Expose skills as MCP Resources under <c>skill://</c> URIs plus a
    /// <c>skill://index.json</c> catalog, per SEP-2640. Default on. SEP-2640 is a pending
    /// proposal — see <see cref="Sep2640Conventions"/> for the isolation seam.
    /// </summary>
    public bool Resources { get; set; } = true;
}

/// <summary>Allow/deny filtering and content integrity pinning.</summary>
public sealed class SkillGovernanceOptions
{
    /// <summary>Skill names that may be served. Empty means all discovered skills are allowed.</summary>
    public List<string> AllowedSkills { get; set; } = new();

    /// <summary>Skill names that must never be served. Deny wins over allow.</summary>
    public List<string> DeniedSkills { get; set; } = new();

    /// <summary>Integrity pinning configuration.</summary>
    public SkillIntegrityOptions Integrity { get; set; } = new();
}

/// <summary>
/// Optional integrity pinning: a skill listed in <see cref="Sha256"/> must hash to the pinned
/// value (see <see cref="SkillHasher"/> for the deterministic scheme) or the configured
/// <see cref="Mode"/> applies.
/// </summary>
public sealed class SkillIntegrityOptions
{
    /// <summary>
    /// What happens on a hash mismatch: <c>warn</c> (serve, log a warning — the default) or
    /// <c>block</c> (treat the skill as denied).
    /// </summary>
    public string Mode { get; set; } = "warn";

    /// <summary>Skill name → expected lowercase hex SHA-256 of the skill folder content.</summary>
    public Dictionary<string, string> Sha256 { get; set; } = new();
}
