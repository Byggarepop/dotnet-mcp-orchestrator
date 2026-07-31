using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator.Orchestration.Skills;

/// <summary>
/// A skill source backed by a git repository, synchronized into a local cache folder with the
/// <c>git</c> CLI (shallow clone, then fetch + hard reset on each refresh) and scanned like a
/// directory source. The CLI is used deliberately — no native libgit2 dependency to keep the
/// Native-AOT/PackAsTool posture, and the user's installed credential helpers keep working.
/// A machine without <c>git</c> logs a warning and yields no skills; never fatal.
/// </summary>
internal sealed class GitSkillSource : ISkillSource
{
    /// <summary>Seconds allowed for a single git command before it is killed.</summary>
    internal const int GitTimeoutSeconds = 60;

    private readonly string _url;
    private readonly string? _gitRef;
    private readonly string? _token;
    private readonly string _cacheDir;
    private readonly ILogger _logger;

    internal GitSkillSource(string id, string url, string? gitRef, string? token, string cacheRoot, ILogger logger)
    {
        Id = id;
        _url = url;
        _gitRef = gitRef;
        _token = token;
        _cacheDir = Path.Combine(cacheRoot, Sanitize(id));
        _logger = logger;
    }

    public string Id { get; }

    public async Task<IReadOnlyList<SkillSnapshot>> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!await SyncAsync(cancellationToken))
            {
                // Sync failed but an earlier clone may still exist — serve the stale copy rather
                // than dropping every skill on a transient network error (last-known-good).
                if (!Directory.Exists(Path.Combine(_cacheDir, ".git")))
                {
                    return [];
                }

                _logger.LogWarning("skill source {Source}: git refresh failed; serving the cached checkout", Id);
            }

            return SkillDirectoryScanner.Scan(_cacheDir, Id, _logger);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "skill source {Source}: unexpected git failure; no skills loaded", Id);
            return [];
        }
    }

    /// <summary>Clones on first use, otherwise fetches and hard-resets to the remote state.</summary>
    private async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        if (!Directory.Exists(Path.Combine(_cacheDir, ".git")))
        {
            Directory.CreateDirectory(_cacheDir);
            var cloneArgs = new List<string> { "clone", "--depth", "1" };
            if (!string.IsNullOrEmpty(_gitRef))
            {
                cloneArgs.AddRange(["--branch", _gitRef]);
            }

            cloneArgs.AddRange([_url, _cacheDir]);
            return await RunGitAsync(cloneArgs, cancellationToken);
        }

        var refSpec = string.IsNullOrEmpty(_gitRef) ? "HEAD" : _gitRef;
        if (!await RunGitAsync(["-C", _cacheDir, "fetch", "--depth", "1", "origin", refSpec], cancellationToken))
        {
            return false;
        }

        return await RunGitAsync(["-C", _cacheDir, "reset", "--hard", "FETCH_HEAD"], cancellationToken);
    }

    /// <summary>
    /// Runs one git command. The token travels as an <c>http.extraHeader</c> config argument —
    /// never on the URL (which would leak into logs and shell history) — and the argument list
    /// is redacted before logging.
    /// </summary>
    private async Task<bool> RunGitAsync(List<string> args, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        if (!string.IsNullOrEmpty(_token))
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add($"http.extraHeader=Authorization: Bearer {_token}");
        }

        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start())
            {
                _logger.LogWarning("skill source {Source}: failed to start git; source skipped", Id);
                return false;
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _logger.LogWarning("skill source {Source}: git is not installed or not on PATH; source skipped ({Message})", Id, ex.Message);
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(GitTimeoutSeconds));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            _logger.LogWarning("skill source {Source}: git {Args} timed out after {Timeout}s", Id, string.Join(' ', args), GitTimeoutSeconds);
            return false;
        }

        if (process.ExitCode != 0)
        {
            var stderr = (await stderrTask).Trim();
            _logger.LogWarning(
                "skill source {Source}: git {Args} exited {Code}: {Stderr}",
                Id, string.Join(' ', args), process.ExitCode, Truncate(stderr));
            return false;
        }

        return true;
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "…";

    /// <summary>Keeps the cache folder name filesystem-safe regardless of what the config id contains.</summary>
    private static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
