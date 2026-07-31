using System.Security.Cryptography;
using System.Text;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// Deterministic content hash of a skill folder, used for integrity pinning and audit logs.
/// </summary>
/// <remarks>
/// Scheme: files are ordered by ordinal comparison of their <c>/</c>-normalized relative paths;
/// for each file the UTF-8 bytes of the path, a single <c>0x00</c> separator, the raw content
/// bytes, and a trailing <c>0x00</c> are fed to one SHA-256 stream. The result is lowercase hex.
/// Reproducible outside the orchestrator, e.g. (bash, from the skill folder):
/// <code>
/// find . -type f | sed 's|^\./||' | LC_ALL=C sort |
///   while read f; do printf '%s\0' "$f"; cat "$f"; printf '\0'; done | sha256sum
/// </code>
/// </remarks>
internal static class SkillHasher
{
    /// <summary>Computes the lowercase-hex SHA-256 for a set of skill files.</summary>
    internal static string ComputeHex(IReadOnlyList<SkillFile> files)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        foreach (var file in files.OrderBy(f => f.RelativePath, StringComparer.Ordinal))
        {
            sha.AppendData(Encoding.UTF8.GetBytes(file.RelativePath));
            sha.AppendData([0]);
            sha.AppendData(file.Content);
            sha.AppendData([0]);
        }

        return Convert.ToHexStringLower(sha.GetHashAndReset());
    }
}
