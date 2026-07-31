using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Applies <c>${VAR}</c> substitution and validation to the <c>skills</c> config section,
/// with the same semantics as capability resolution (built-in placeholders first, then env
/// vars). Invalid sources are dropped with a log line — the skills section is never a reason
/// to reject an otherwise valid config, matching "invalid skills are logged and skipped".
/// </summary>
internal static class SkillsConfigResolver
{
    /// <summary>
    /// Resolves and validates the section in place. Returns <c>null</c> when absent or disabled.
    /// When <paramref name="forbidLocalPlaceholders"/> is set (centrally served configs),
    /// <c>directory</c> sources using <c>${CONFIG_DIR}</c>/<c>${SOLUTION_DIR}</c> are dropped —
    /// those resolve to machine-local paths that mean nothing in a shared catalog.
    /// </summary>
    internal static SkillsOptions? Resolve(
        SkillsOptions? options,
        IReadOnlyDictionary<string, string> placeholders,
        bool forbidLocalPlaceholders,
        ILogger logger)
    {
        if (options is null || !options.Enabled)
        {
            return null;
        }

        var kept = new List<SkillSourceOptions>();
        var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var source in options.Sources)
        {
            if (string.IsNullOrWhiteSpace(source.Id))
            {
                logger.LogWarning("skills config: a source has no id; source skipped");
                continue;
            }

            if (!seenIds.Add(source.Id))
            {
                logger.LogWarning("skills config: duplicate source id '{Id}'; the first definition wins", source.Id);
                continue;
            }

            if (forbidLocalPlaceholders && UsesLocalPlaceholder(source.Path))
            {
                logger.LogWarning(
                    "skills config: source '{Id}' uses a machine-local placeholder in a centrally served "
                    + "config; source skipped (use ${{ENV_VAR}} or absolute paths)", source.Id);
                continue;
            }

            source.Path = SubstituteOrNull(source.Path, placeholders, logger);
            source.Url = SubstituteOrNull(source.Url, placeholders, logger);
            source.Token = SubstituteOrNull(source.Token, placeholders, logger);
            source.IndexUrl = SubstituteOrNull(source.IndexUrl, placeholders, logger);
            source.Authorization = SubstituteOrNull(source.Authorization, placeholders, logger);

            var problem = source.Type.ToLowerInvariant() switch
            {
                "directory" => string.IsNullOrWhiteSpace(source.Path) ? "directory source has no path" : null,
                "git" => string.IsNullOrWhiteSpace(source.Url) ? "git source has no url" : null,
                "http" => !Uri.TryCreate(source.IndexUrl, UriKind.Absolute, out var uri)
                          || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp)
                    ? "http source needs an absolute http(s) indexUrl"
                    : null,
                _ => $"unknown source type '{source.Type}'",
            };

            if (problem is not null)
            {
                logger.LogWarning("skills config: source '{Id}' invalid: {Problem}; source skipped", source.Id, problem);
                continue;
            }

            kept.Add(source);
        }

        options.Sources = kept;
        return options;
    }

    private static string? SubstituteOrNull(
        string? value, IReadOnlyDictionary<string, string> placeholders, ILogger logger)
        => value is null ? null : CapabilityCatalog.Substitute(value, placeholders, logger);

    private static bool UsesLocalPlaceholder(string? value)
        => value is not null
           && (value.Contains("${CONFIG_DIR}", StringComparison.OrdinalIgnoreCase)
               || value.Contains("${SOLUTION_DIR}", StringComparison.OrdinalIgnoreCase));
}
