using System.IO.Compression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Update;

/// <summary>
/// Opt-in self-update for the <b>Native-AOT binary</b> (Approach A: stage-and-swap-on-next-launch).
///
/// <para>Why not restart in place: this is an MCP server over stdio — a child process whose stdin/
/// stdout the host owns. A self-terminate-and-relaunch would lose those inherited pipes and drop the
/// session. So we never restart; we download the newer build, verify its checksum, and atomically
/// swap the on-disk binary. The host runs the new version the <i>next</i> time it launches the
/// server (which it does every session), with no interruption to the current one.</para>
///
/// <para>Guardrails: it runs only when <c>MCP_ORCHESTRATOR_AUTOUPDATE</c> is set <i>and</i> the
/// process is the native binary (a framework-dependent <c>dotnet tool</c> install updates via
/// <c>dotnet tool update</c> instead). Downloads are checksum-verified against the release's
/// <c>SHA256SUMS</c>. Every step is best-effort and swallowed — a failed update must never affect
/// the running server.</para>
/// </summary>
internal static class SelfUpdater
{
    private const string DefaultRepo = "Byggarepop/dotnet-mcp-orchestrator";
    private const string EnableEnv = "MCP_ORCHESTRATOR_AUTOUPDATE";
    private const string RepoEnv = "MCP_ORCHESTRATOR_UPDATE_REPO";
    private static readonly TimeSpan CheckInterval = TimeSpan.FromHours(12);

    /// <summary>
    /// Enabled only when opted in via env AND running as the native binary. Under the JIT/dotnet-tool
    /// path dynamic code is supported, so we defer to <c>dotnet tool update</c> and stay out of it.
    /// </summary>
    public static bool IsEnabled =>
        IsTruthy(Environment.GetEnvironmentVariable(EnableEnv)) && !RuntimeFeature.IsDynamicCodeSupported;

    /// <summary>Removes a leftover renamed-aside binary from a prior swap. Best-effort; call at startup.</summary>
    public static void CleanupOldBinary()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (exe is null) return;
            var dir = Path.GetDirectoryName(exe);
            if (dir is null) return;
            foreach (var stale in Directory.EnumerateFiles(dir, Path.GetFileName(exe) + ".old*"))
            {
                try { File.Delete(stale); } catch { /* still locked by the previous process; next time */ }
            }
        }
        catch { /* best-effort */ }
    }

    /// <summary>
    /// Checks GitHub for a newer release and, if found, stages it so the next launch runs it. Never
    /// throws — all failures are logged at debug and swallowed.
    /// </summary>
    public static async Task CheckAndStageAsync(ILogger logger, CancellationToken ct)
    {
        try
        {
            if (!ShouldCheckNow())
            {
                return;
            }

            var exePath = Environment.ProcessPath;
            if (exePath is null)
            {
                return;
            }

            var rid = CurrentRid();
            if (rid is null)
            {
                logger.LogDebug("Self-update: unsupported platform; skipping.");
                return;
            }

            var current = NormalizedAssemblyVersion();
            var repo = Environment.GetEnvironmentVariable(RepoEnv) ?? DefaultRepo;

            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("McpOrchestrator-selfupdate");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");

            // 1. Latest release metadata.
            var json = await http.GetStringAsync(
                $"https://api.github.com/repos/{repo}/releases/latest", ct);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
            var latest = ParseVersion(tag);
            if (latest is null)
            {
                return;
            }
            if (latest <= current)
            {
                logger.LogDebug("Self-update: already current ({Current}); latest is {Latest}.", current, latest);
                return;
            }

            // 2. Resolve the asset URLs for this RID plus the checksum manifest.
            var assetName = $"McpOrchestrator-{tag!.TrimStart('v')}-{rid}.zip";
            string? zipUrl = null, sumsUrl = null;
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
                    if (name == assetName) zipUrl = url;
                    else if (name == "SHA256SUMS") sumsUrl = url;
                }
            }
            if (zipUrl is null || sumsUrl is null)
            {
                logger.LogDebug("Self-update: no {Asset} (or SHA256SUMS) in release {Tag}; skipping.", assetName, tag);
                return;
            }

            // 3. Download the package and its expected hash, and verify.
            var zipBytes = await http.GetByteArrayAsync(zipUrl, ct);
            var sums = await http.GetStringAsync(sumsUrl, ct);
            var expected = FindHash(sums, assetName);
            var actual = Convert.ToHexString(SHA256.HashData(zipBytes)).ToLowerInvariant();
            if (expected is null || !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogWarning("Self-update: checksum mismatch for {Asset}; refusing to apply.", assetName);
                return;
            }

            // 4. Extract just the executable, then swap it in for next launch.
            var staged = StageExecutable(zipBytes, exePath);
            if (staged is null)
            {
                logger.LogDebug("Self-update: executable not found inside {Asset}.", assetName);
                return;
            }

            ApplyStagedBinary(exePath, staged);
            logger.LogInformation(
                "Self-update: staged {Latest} (from {Current}); it will take effect on the next launch.",
                latest, current);
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Self-update: check/stage failed; will retry next time.");
        }
    }

    /// <summary>Extracts the running executable's counterpart from the zip to a temp file beside it.</summary>
    private static string? StageExecutable(byte[] zipBytes, string exePath)
    {
        var exeName = Path.GetFileName(exePath);
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = archive.GetEntry(exeName)
            ?? archive.Entries.FirstOrDefault(e => string.Equals(e.Name, exeName, StringComparison.OrdinalIgnoreCase));
        if (entry is null)
        {
            return null;
        }

        var staged = exePath + ".new";
        if (File.Exists(staged)) File.Delete(staged);
        entry.ExtractToFile(staged, overwrite: true);
        return staged;
    }

    /// <summary>
    /// Replaces the on-disk binary with the staged one. The running process is unaffected — on Unix it
    /// keeps the old inode; on Windows the live exe is renamed aside (allowed) and cleaned up later.
    /// </summary>
    private static void ApplyStagedBinary(string exePath, string stagedExe)
    {
        if (OperatingSystem.IsWindows())
        {
            var oldPath = exePath + ".old";
            try { if (File.Exists(oldPath)) File.Delete(oldPath); }
            catch { oldPath = exePath + ".old-" + DateTime.UtcNow.Ticks; } // previous copy still locked

            File.Move(exePath, oldPath);          // rename the running exe out of the way
            try
            {
                File.Move(stagedExe, exePath);     // put the new one in its place
            }
            catch
            {
                File.Move(oldPath, exePath);        // rollback so we never leave the binary missing
                throw;
            }
        }
        else
        {
            File.SetUnixFileMode(stagedExe,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            File.Move(stagedExe, exePath, overwrite: true); // atomic replace; running process keeps old inode
        }
    }

    /// <summary>Throttles to one network check per <see cref="CheckInterval"/> via a temp marker file.</summary>
    private static bool ShouldCheckNow()
    {
        try
        {
            var marker = Path.Combine(Path.GetTempPath(), "mcp-orchestrator-update-check");
            if (File.Exists(marker) && DateTime.UtcNow - File.GetLastWriteTimeUtc(marker) < CheckInterval)
            {
                return false;
            }
            File.WriteAllText(marker, DateTime.UtcNow.ToString("o"));
            return true;
        }
        catch
        {
            return true; // if the marker can't be written, don't block the check
        }
    }

    internal static string? FindHash(string sums, string assetName)
    {
        // sha256sum format: "<hex>  <filename>" (filename may be bare or have a leading ./).
        foreach (var line in sums.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0) continue;
            var parts = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 2 && parts[^1].TrimStart('.', '/', '\\') == assetName)
            {
                return parts[0];
            }
        }
        return null;
    }

    private static string? CurrentRid()
    {
        var os = OperatingSystem.IsWindows() ? "win"
            : OperatingSystem.IsLinux() ? "linux"
            : OperatingSystem.IsMacOS() ? "osx"
            : null;
        var arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => null,
        };
        return os is null || arch is null ? null : $"{os}-{arch}";
    }

    private static Version NormalizedAssemblyVersion() =>
        Normalize(Assembly.GetEntryAssembly()?.GetName().Version) ?? new Version(0, 0, 0);

    internal static Version? ParseVersion(string? tag) =>
        tag is null ? null : Normalize(Version.TryParse(tag.TrimStart('v'), out var v) ? v : null);

    /// <summary>Collapses a <see cref="Version"/> to major.minor.build (revision/-1 treated as 0).</summary>
    private static Version? Normalize(Version? v) =>
        v is null ? null : new Version(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);

    private static bool IsTruthy(string? value) =>
        value is "1" or "true" or "yes" or "on" ||
        string.Equals(value, "true", StringComparison.OrdinalIgnoreCase);
}
