using ModelContextProtocol.Protocol;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Derives the MCP <c>roots</c> the orchestrator advertises to one downstream server: the
/// directories that server may treat as its workspace (e.g. where chrome-devtools-mcp is allowed
/// to write screenshots). Each capability gets only its own configured directories — explicit
/// <see cref="CapabilityDescriptor.Roots"/>, else its <see cref="CapabilityDescriptor.WorkingDirectory"/>
/// — never the orchestrator's own working directory, and a capability with neither gets no
/// roots at all (so the <c>roots</c> capability is not advertised for it).
/// </summary>
public static class DownstreamRoots
{
    /// <summary>
    /// The roots for <paramref name="descriptor"/>: one <see cref="Root"/> per configured
    /// directory, as an absolute <c>file:</c> URI named after the directory's last segment.
    /// Empty when the capability configures neither <c>roots</c> nor <c>workingDirectory</c>.
    /// </summary>
    public static IReadOnlyList<Root> For(CapabilityDescriptor descriptor)
    {
        IEnumerable<string> directories = descriptor.Roots.Count > 0
            ? descriptor.Roots
            : descriptor.WorkingDirectory is { } wd ? [wd] : [];

        var roots = new List<Root>();
        var seen = new HashSet<string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        foreach (var directory in directories)
        {
            if (string.IsNullOrWhiteSpace(directory))
            {
                continue;
            }

            var full = TrimTrailingSeparators(Path.GetFullPath(directory));
            if (!seen.Add(full))
            {
                continue;
            }

            var name = Path.GetFileName(full);
            roots.Add(new Root
            {
                Uri = ToFileUri(full),
                Name = string.IsNullOrEmpty(name) ? full : name,
            });
        }

        return roots;
    }

    /// <summary>
    /// Converts an absolute path to a <c>file:</c> URI the way MCP clients on every platform
    /// produce them: <c>C:\a b\c</c> → <c>file:///C:/a%20b/c</c>, <c>\srv\share\c</c> →
    /// <c>file://srv/share/c</c>, <c>/home/c</c> → <c>file:///home/c</c>. Every segment is
    /// percent-encoded, so <c>#</c>, <c>?</c>, <c>%</c> and non-ASCII characters round-trip
    /// through a downstream's <c>fileURLToPath</c>. Pure string work — no filesystem access.
    /// </summary>
    public static string ToFileUri(string absolutePath)
    {
        var p = absolutePath.Replace('\\', '/');
        string prefix;
        string rest;

        if (p.StartsWith("//", StringComparison.Ordinal))
        {
            // UNC: the host is the URI authority.
            var slash = p.IndexOf('/', 2);
            var host = slash < 0 ? p[2..] : p[2..slash];
            rest = slash < 0 ? string.Empty : p[(slash + 1)..];
            prefix = "file://" + host + "/";
        }
        else if (p.Length >= 2 && char.IsAsciiLetter(p[0]) && p[1] == ':')
        {
            // Drive-rooted Windows path. The drive letter is not a segment to encode.
            prefix = "file:///" + char.ToUpperInvariant(p[0]) + ":/";
            rest = p.Length > 2 ? p[2..] : string.Empty;
        }
        else
        {
            prefix = "file:///";
            rest = p;
        }

        var segments = rest
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        return prefix + string.Join('/', segments);
    }

    /// <summary>Strips trailing separators except on a bare root (<c>C:\</c>, <c>/</c>).</summary>
    private static string TrimTrailingSeparators(string path)
    {
        var trimmed = path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 || (trimmed.Length == 2 && trimmed[1] == ':') ? path : trimmed;
    }
}
