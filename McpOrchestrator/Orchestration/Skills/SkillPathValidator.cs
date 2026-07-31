namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Validates and normalizes a client-supplied relative path before it is used as a snapshot
/// lookup key. Serving is dictionary-based (see <see cref="SkillSnapshot.TryGetFile"/>), so this
/// is defense in depth: even a validator bug cannot escape the snapshot — but rejecting early
/// gives the client a precise error instead of a generic "not found".
/// </summary>
internal static class SkillPathValidator
{
    /// <summary>
    /// Returns true and the normalized <c>/</c>-separated path when <paramref name="relativePath"/>
    /// is a plain relative path inside the skill folder. Rejects empty paths, rooted paths
    /// (leading separator or a drive letter), any <c>.</c>/<c>..</c> segment, percent-encoded
    /// traversal (<c>%2e</c>), and embedded NUL. Backslashes are normalized to <c>/</c> first,
    /// so <c>..\</c> tricks are caught by the same segment check.
    /// </summary>
    internal static bool TryNormalize(string? relativePath, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(relativePath) || relativePath.Contains('\0'))
        {
            return false;
        }

        var candidate = relativePath.Replace('\\', '/');
        if (candidate.StartsWith('/') || candidate.Contains(':', StringComparison.Ordinal))
        {
            return false;
        }

        // Percent-encoded dots would decode to traversal on a careless consumer downstream.
        if (candidate.Contains("%2e", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("%2f", StringComparison.OrdinalIgnoreCase) ||
            candidate.Contains("%5c", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var segment in candidate.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or "..")
            {
                return false;
            }
        }

        normalized = candidate;
        return true;
    }
}
