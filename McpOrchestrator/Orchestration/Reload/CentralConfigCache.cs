using System.Text.Json;
using System.Text.Json.Serialization;

namespace McpOrchestrator.Orchestration.Reload;

/// <summary>What is known about the cached central config: where it came from and its HTTP validators.</summary>
internal sealed record CentralCacheMeta(
    string Url,
    string? ETag,
    string? LastModified,
    DateTimeOffset FetchedAtUtc);

/// <summary>AOT-safe serialization context for the cache sidecar.</summary>
[JsonSerializable(typeof(CentralCacheMeta))]
internal sealed partial class CentralCacheJsonContext : JsonSerializerContext;

/// <summary>
/// The on-disk copy of the last good centrally fetched config
/// (<c>~/.mcpOrchestrator/central-config-cache.json</c> plus a sidecar with URL, ETag and
/// timestamp). It serves two purposes: offline startup (the URL is unreachable but a cached copy
/// for the same URL exists) and the byte source the reload pipeline loads from after each fetch.
/// Writes are atomic (temp file + rename), so a crash mid-write can never leave a torn config
/// behind as "last known good".
/// </summary>
internal sealed class CentralConfigCache
{
    private readonly string _payloadPath;
    private readonly string _metaPath;

    /// <param name="directory">Override for tests; defaults to <c>~/.mcpOrchestrator</c>.</param>
    public CentralConfigCache(string? directory = null)
    {
        directory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".mcpOrchestrator");
        _payloadPath = Path.Combine(directory, "central-config-cache.json");
        _metaPath = Path.Combine(directory, "central-config-cache.meta.json");
    }

    /// <summary>Where the cached payload lives — the reload pipeline reads from here.</summary>
    public string PayloadPath => _payloadPath;

    /// <summary>Atomically stores a validated payload and its sidecar (payload first, so a crash
    /// between the two writes leaves stale validators, which merely costs one full re-fetch).</summary>
    public void Write(string payload, CentralCacheMeta meta)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_payloadPath)!);
        WriteAtomically(_payloadPath, payload);
        WriteAtomically(_metaPath, JsonSerializer.Serialize(meta, CentralCacheJsonContext.Default.CentralCacheMeta));
    }

    /// <summary>
    /// Reads the cached payload — but only when it was fetched from <paramref name="url"/>.
    /// A cache recorded for a different URL is treated as no cache at all: silently serving
    /// yesterday's catalog from some other endpoint would be worse than failing.
    /// </summary>
    public (string Payload, CentralCacheMeta Meta)? TryRead(string url)
    {
        try
        {
            if (!File.Exists(_payloadPath) || !File.Exists(_metaPath))
            {
                return null;
            }

            var meta = JsonSerializer.Deserialize(File.ReadAllText(_metaPath), CentralCacheJsonContext.Default.CentralCacheMeta);
            if (meta is null || !string.Equals(meta.Url, url, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            return (File.ReadAllText(_payloadPath), meta);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null; // An unreadable cache is no cache.
        }
    }

    private static void WriteAtomically(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}
