using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Loads the downstream capability catalog from a JSON config file and resolves
/// <c>${VAR}</c> placeholders in launch commands/paths/env. Resolution order for each
/// placeholder: built-in placeholders (<c>CONFIG_DIR</c>, <c>SOLUTION_DIR</c>) first, then
/// process environment variables; unknown placeholders are left untouched (and logged).
/// </summary>
public sealed partial class CapabilityCatalog : ICapabilityCatalog
{
    public IReadOnlyList<CapabilityDescriptor> Capabilities { get; }
    public IReadOnlyList<string> Names { get; }

    /// <summary>
    /// The config file this catalog was loaded from, or <c>null</c> when built in memory
    /// (<see cref="FromDescriptors"/>). Hot reload watches this path.
    /// </summary>
    public string? SourcePath { get; }

    private readonly Dictionary<string, CapabilityDescriptor> _byName;

    /// <summary>Private constructor; build instances via <see cref="Load"/> or <see cref="FromDescriptors"/>.</summary>
    private CapabilityCatalog(IReadOnlyList<CapabilityDescriptor> capabilities, string? sourcePath = null)
    {
        Capabilities = capabilities;
        Names = capabilities.Select(c => c.Name).ToArray();
        SourcePath = sourcePath;
        _byName = capabilities.ToDictionary(c => c.Name, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Finds a capability by name (case-insensitive), or <c>null</c> if none matches.</summary>
    public CapabilityDescriptor? Find(string name) =>
        name is not null && _byName.TryGetValue(name, out var d) ? d : null;

    /// <summary>
    /// Builds a catalog from already-loaded descriptors, applying the same validation as
    /// <see cref="Load"/>: drops entries that are disabled, unnamed, lack a launch command,
    /// or duplicate an earlier name (case-insensitive, first one wins). Exposed for tests.
    /// </summary>
    internal static CapabilityCatalog FromDescriptors(
        IEnumerable<CapabilityDescriptor> capabilities, ILogger logger, string? sourcePath = null)
    {
        var kept = new List<CapabilityDescriptor>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var c in capabilities)
        {
            if (!c.Enabled)
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(c.Name))
            {
                logger.LogWarning("Skipping a capability with no name.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(c.Command))
            {
                logger.LogWarning("Skipping capability '{Name}': no launch command configured.", c.Name);
                continue;
            }

            if (!seen.Add(c.Name))
            {
                logger.LogWarning(
                    "Duplicate capability name '{Name}' ignored; the first definition wins.", c.Name);
                continue;
            }

            kept.Add(c);
        }

        return new CapabilityCatalog(kept, sourcePath);
    }


    /// <summary>
    /// Loads the catalog. The config path comes from <c>MCP_ORCHESTRATOR_CONFIG</c>
    /// if set, otherwise <c>orchestrator.config.json</c> in <paramref name="contentRoot"/>.
    /// A missing file yields an empty (but valid) catalog so the server still starts.
    /// </summary>
    /// <param name="contentRoot">The server's content root (its project directory under <c>dotnet run</c>).</param>
    public static CapabilityCatalog Load(string contentRoot, ILogger logger)
    {
        // Anchor on the solution dir (found by walking up for the .slnx), not the current
        // directory — under `dotnet run` the cwd is the caller's, not the project's.
        var solutionDir =
            FindAncestorContaining("McpOrchestrator.slnx", AppContext.BaseDirectory)
            ?? FindAncestorContaining("McpOrchestrator.slnx", contentRoot)
            ?? contentRoot;

        var configPath = ResolveConfigPath(contentRoot, solutionDir);
        if (configPath is null)
        {
            logger.LogWarning(
                "Orchestrator config not found (searched MCP_ORCHESTRATOR_CONFIG, " +
                "{Sln}/McpOrchestrator, {Base}, {Root}). Starting with no capabilities.",
                solutionDir, AppContext.BaseDirectory, contentRoot);
            return new CapabilityCatalog(Array.Empty<CapabilityDescriptor>());
        }

        OrchestratorConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(File.ReadAllText(configPath), OrchestratorConfigJsonContext.Default.OrchestratorConfig);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Never let a bad/locked/unreadable config crash startup — degrade to an empty
            // catalog so the server still comes up (and the problem is visible in the log).
            logger.LogError(ex, "Failed to read or parse orchestrator config at {ConfigPath}.", configPath);
            return new CapabilityCatalog(Array.Empty<CapabilityDescriptor>());
        }

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONFIG_DIR"] = Path.GetDirectoryName(configPath) ?? contentRoot,
            // The sample config locates sibling downstream projects via ${SOLUTION_DIR}.
            ["SOLUTION_DIR"] = solutionDir,
        };

        // Resolve ${VAR} placeholders first, then validate/dedupe. Disabled entries are skipped
        // before resolution so their placeholders never need to resolve.
        var resolved = (config?.Capabilities ?? new())
            .Where(c => c.Enabled)
            .Select(c => Resolve(c, placeholders, logger));

        var catalog = FromDescriptors(resolved, logger, configPath);

        logger.LogInformation(
            "Loaded {Count} capability/capabilities from {ConfigPath}: {Names}",
            catalog.Capabilities.Count, configPath, string.Join(", ", catalog.Names));

        return catalog;
    }

    /// <summary>
    /// Reloads the config for hot reload: parse, resolve placeholders, and validate — strictly.
    /// Unlike startup (<see cref="Load"/>), which leniently drops bad entries so the server always
    /// comes up, a reload has a running config to protect: any problem (malformed JSON, an enabled
    /// entry missing its name or command, duplicate names) logs an error and returns <c>null</c>,
    /// and the caller keeps the current catalog untouched (last-known-good).
    /// </summary>
    /// <returns>
    /// The new enabled catalog plus <em>every</em> resolved entry including disabled ones — the
    /// differ needs the full list so an <c>enabled</c> toggle is a metadata change, not a
    /// remove/add of the live downstream.
    /// </returns>
    internal static ReloadedConfig? TryLoadForReload(string configPath, ILogger logger)
    {
        var fullPath = Path.GetFullPath(configPath);

        string text;
        try
        {
            text = File.ReadAllText(fullPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            logger.LogError(
                ex, "Config reload rejected: failed to read {ConfigPath}. Keeping the current config.", fullPath);
            return null;
        }

        var configDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var solutionDir =
            FindAncestorContaining("McpOrchestrator.slnx", AppContext.BaseDirectory)
            ?? FindAncestorContaining("McpOrchestrator.slnx", configDir)
            ?? configDir;

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONFIG_DIR"] = configDir,
            ["SOLUTION_DIR"] = solutionDir,
        };

        return TryParseForReload(text, fullPath, placeholders, forbidLocalPlaceholders: false, logger);
    }

    /// <summary>
    /// The text half of <see cref="TryLoadForReload"/>, shared with centrally served configs
    /// (which arrive over HTTP rather than from a file). Same strictness; additionally, when
    /// <paramref name="forbidLocalPlaceholders"/> is set, <c>${CONFIG_DIR}</c> and
    /// <c>${SOLUTION_DIR}</c> reject the config — they resolve to machine-local paths that mean
    /// nothing in a shared catalog; <c>${ENV_VAR}</c> or absolute paths are the supported way.
    /// </summary>
    internal static ReloadedConfig? TryParseForReload(
        string configText,
        string sourceLabel,
        IReadOnlyDictionary<string, string> builtinPlaceholders,
        bool forbidLocalPlaceholders,
        ILogger logger)
    {
        OrchestratorConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(configText, OrchestratorConfigJsonContext.Default.OrchestratorConfig);
        }
        catch (JsonException ex)
        {
            logger.LogError(
                ex, "Config reload rejected: failed to parse {Source}. Keeping the current config.", sourceLabel);
            return null;
        }

        var entries = config?.Capabilities ?? new();

        if (forbidLocalPlaceholders)
        {
            var forbidden = FindForbiddenPlaceholder(entries);
            if (forbidden is not null)
            {
                logger.LogError(
                    "Config reload rejected: {Source} uses ${{{Placeholder}}}, which resolves to a "
                    + "machine-local path and is invalid in a centrally served config. Use ${{ENV_VAR}} "
                    + "(resolved on each consuming machine) or absolute paths instead. Keeping the current config.",
                    sourceLabel, forbidden);
                return null;
            }
        }

        // Resolve every entry — disabled ones too, so the differ compares launch fields on equal
        // footing across an enabled/disabled flip.
        var all = entries
            .Select(c => Resolve(c, builtinPlaceholders, logger))
            .ToList();

        // Strict validation over the enabled entries: reject the whole reload rather than silently
        // dropping the offender (a running session must never lose a capability to a typo).
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var c in all.Where(c => c.Enabled))
        {
            var problem =
                string.IsNullOrWhiteSpace(c.Name) ? "an entry has no name"
                : string.IsNullOrWhiteSpace(c.Command) ? $"capability '{c.Name}' has no launch command"
                : !seen.Add(c.Name) ? $"duplicate capability name '{c.Name}'"
                : null;

            if (problem is not null)
            {
                logger.LogError(
                    "Config reload rejected: {Problem} in {Source}. Keeping the current config.", problem, sourceLabel);
                return null;
            }
        }

        return new ReloadedConfig(FromDescriptors(all, logger, sourceLabel), all);
    }

    /// <summary>
    /// Returns the name of the first machine-local placeholder (<c>CONFIG_DIR</c> /
    /// <c>SOLUTION_DIR</c>) used anywhere in a substitutable field, or <c>null</c> when clean.
    /// </summary>
    private static string? FindForbiddenPlaceholder(IEnumerable<CapabilityDescriptor> entries)
    {
        foreach (var c in entries)
        {
            var fields = new[] { c.Command, c.WorkingDirectory }
                .Concat(c.Args)
                .Concat(c.Env.Values);
            foreach (var value in fields)
            {
                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }

                foreach (System.Text.RegularExpressions.Match match in PlaceholderRegex().Matches(value))
                {
                    var key = match.Groups[1].Value;
                    if (key.Equals("CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                        || key.Equals("SOLUTION_DIR", StringComparison.OrdinalIgnoreCase))
                    {
                        return key;
                    }
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Loads the catalog from an explicit file path (used by the <c>profile</c> command). Unlike
    /// <see cref="Load"/>, which degrades to an empty catalog so the server always starts, this
    /// throws on a missing or unparseable file — a CLI told exactly which file to read should fail
    /// loudly rather than silently profile nothing. Applies the same <c>${CONFIG_DIR}</c> /
    /// <c>${SOLUTION_DIR}</c> / <c>${ENV_VAR}</c> substitution as <see cref="Load"/>.
    /// </summary>
    /// <exception cref="FileNotFoundException">The config file does not exist.</exception>
    /// <exception cref="InvalidDataException">The config file could not be read or parsed.</exception>
    internal static CapabilityCatalog LoadFromFile(string configPath, ILogger logger)
    {
        var fullPath = Path.GetFullPath(configPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException($"Orchestrator config not found at '{fullPath}'.", fullPath);
        }

        OrchestratorConfig? config;
        try
        {
            config = JsonSerializer.Deserialize(File.ReadAllText(fullPath), OrchestratorConfigJsonContext.Default.OrchestratorConfig);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"Failed to read or parse orchestrator config at '{fullPath}': {ex.Message}", ex);
        }

        var configDir = Path.GetDirectoryName(fullPath) ?? Directory.GetCurrentDirectory();
        var solutionDir =
            FindAncestorContaining("McpOrchestrator.slnx", AppContext.BaseDirectory)
            ?? FindAncestorContaining("McpOrchestrator.slnx", configDir)
            ?? configDir;

        var placeholders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CONFIG_DIR"] = configDir,
            ["SOLUTION_DIR"] = solutionDir,
        };

        var resolved = (config?.Capabilities ?? new())
            .Where(c => c.Enabled)
            .Select(c => Resolve(c, placeholders, logger));

        var catalog = FromDescriptors(resolved, logger, fullPath);
        logger.LogInformation(
            "Loaded {Count} capability/capabilities from {ConfigPath} for profiling.",
            catalog.Capabilities.Count, fullPath);
        return catalog;
    }

    /// <summary>Applies <c>${VAR}</c> substitution in place to a descriptor's command, args, working dir, and env.</summary>
    private static CapabilityDescriptor Resolve(
        CapabilityDescriptor c, IReadOnlyDictionary<string, string> placeholders, ILogger logger)
    {
        c.Command = Substitute(c.Command, placeholders, logger);
        c.Args = c.Args.Select(a => Substitute(a, placeholders, logger)).ToList();
        c.WorkingDirectory = c.WorkingDirectory is null ? null : Substitute(c.WorkingDirectory, placeholders, logger);
        c.Env = c.Env.ToDictionary(
            kv => kv.Key,
            kv => kv.Value is null ? null : Substitute(kv.Value, placeholders, logger));
        return c;
    }

    /// <summary>
    /// Picks the first existing config from: the <c>MCP_ORCHESTRATOR_CONFIG</c>
    /// override, the in-repo source file, the copy next to the assembly, then the content
    /// root. Returns <c>null</c> if none exist.
    /// </summary>
    private static string? ResolveConfigPath(string contentRoot, string solutionDir)
    {
        string?[] candidates =
        {
            Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_CONFIG"),
            Path.Combine(solutionDir, "McpOrchestrator", "orchestrator.config.json"),
            Path.Combine(AppContext.BaseDirectory, "orchestrator.config.json"),
            Path.Combine(contentRoot, "orchestrator.config.json"),
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
            {
                return Path.GetFullPath(candidate);
            }
        }

        return null;
    }

    /// <summary>Walks up from <paramref name="startDir"/> for the first ancestor containing <paramref name="fileName"/>.</summary>
    private static string? FindAncestorContaining(string fileName, string startDir)
    {
        for (var dir = new DirectoryInfo(startDir); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, fileName)))
            {
                return dir.FullName;
            }
        }

        return null;
    }

    /// <summary>Replaces a <c>${VAR}</c> placeholder with a built-in value, then an env var; leaves unknowns as-is.</summary>
    private static string Substitute(string value, IReadOnlyDictionary<string, string> placeholders, ILogger logger)
    {
        if (string.IsNullOrEmpty(value) || !value.Contains("${", StringComparison.Ordinal))
        {
            return value;
        }

        return PlaceholderRegex().Replace(value, match =>
        {
            var key = match.Groups[1].Value;
            if (placeholders.TryGetValue(key, out var builtIn))
            {
                return builtIn;
            }

            var env = Environment.GetEnvironmentVariable(key);
            if (env is not null)
            {
                return env;
            }

            logger.LogWarning("Config placeholder ${{{Key}}} could not be resolved; left as-is.", key);
            return match.Value;
        });
    }

    [GeneratedRegex(@"\$\{([A-Za-z_][A-Za-z0-9_]*)\}")]
    private static partial Regex PlaceholderRegex();
}

/// <summary>
/// A successfully (re)loaded config: the catalog of enabled capabilities that goes live, plus
/// every resolved entry including disabled ones, which the reload differ compares against the
/// previous full list.
/// </summary>
internal sealed record ReloadedConfig(
    CapabilityCatalog Catalog,
    IReadOnlyList<CapabilityDescriptor> AllEntries);
