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
/// <remarks>
/// Every stage is logged so a refresh can be followed without guessing: clone or fetch started
/// (ref, cache path, timeout), completed (duration, commit; for a refresh whether the checkout
/// changed), and on failure whether a cached checkout keeps serving. The URL is logged with any
/// userinfo removed and the token never appears anywhere.
/// </remarks>
internal sealed class GitSkillSource : ISkillSource
{
    /// <summary>Seconds allowed for a single git command before it is killed.</summary>
    internal const int GitTimeoutSeconds = 60;

    private readonly string _url;
    private readonly string? _gitRef;
    private readonly string? _token;
    private readonly string _cacheDir;
    private readonly int? _pollSeconds;
    private readonly ILogger _logger;

    /// <param name="pollSeconds">The configured refresh interval, logged as the retry horizon after a failure; null when unknown.</param>
    internal GitSkillSource(string id, string url, string? gitRef, string? token, string cacheRoot, ILogger logger, int? pollSeconds = null)
    {
        Id = id;
        _url = url;
        _gitRef = gitRef;
        _token = token;
        _cacheDir = Path.Combine(cacheRoot, Sanitize(id));
        _pollSeconds = pollSeconds;
        _logger = logger;
    }

    public string Id { get; }

    /// <summary>The local checkout this source scans.</summary>
    internal string CacheDir => _cacheDir;

    public async Task<SkillSourceLoad> LoadAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await SyncAsync(cancellationToken))
            {
                return SkillSourceLoad.Fresh(SkillDirectoryScanner.Scan(_cacheDir, Id, _logger));
            }

            // Sync failed but an earlier clone may still exist — serve the stale copy rather
            // than dropping every skill on a transient network error (last-known-good).
            var retry = _pollSeconds is { } s ? $"; next refresh in {s}s" : "";
            if (!HasCheckout())
            {
                _logger.LogWarning(
                    "skill source {Source}: no usable checkout under {Cache}; source yields no skills{Retry}",
                    Id, _cacheDir, retry);
                return SkillSourceLoad.Unavailable();
            }

            var commit = await RevParseHeadAsync(cancellationToken);
            _logger.LogWarning(
                "skill source {Source}: git refresh failed; serving the cached checkout at commit {Commit}{Retry}",
                Id, commit ?? "unknown", retry);
            return SkillSourceLoad.Cached(SkillDirectoryScanner.Scan(_cacheDir, Id, _logger));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "skill source {Source}: unexpected git failure; no skills loaded", Id);
            return SkillSourceLoad.Unavailable();
        }
    }

    private bool HasCheckout() => Directory.Exists(Path.Combine(_cacheDir, ".git"));

    /// <summary>Clones on first use, otherwise fetches and hard-resets to the remote state.</summary>
    private async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        var refLabel = string.IsNullOrEmpty(_gitRef) ? "default branch" : _gitRef;
        var stopwatch = Stopwatch.StartNew();

        if (!HasCheckout())
        {
            Directory.CreateDirectory(_cacheDir);
            _logger.LogInformation(
                "skill source {Source}: clone started; url={Url}; ref={Ref}; cache={Cache}; timeout={Timeout}s",
                Id, RedactUrl(_url), refLabel, _cacheDir, GitTimeoutSeconds);

            var cloneArgs = new List<string> { "clone", "--depth", "1" };
            if (!string.IsNullOrEmpty(_gitRef))
            {
                cloneArgs.AddRange(["--branch", _gitRef]);
            }

            cloneArgs.AddRange([_url, _cacheDir]);
            if (!await RunGitAsync(cloneArgs, cancellationToken))
            {
                return false;
            }

            _logger.LogInformation(
                "skill source {Source}: clone completed; duration={Duration}ms; commit={Commit}",
                Id, stopwatch.ElapsedMilliseconds, await RevParseHeadAsync(cancellationToken) ?? "unknown");
            return true;
        }

        _logger.LogInformation(
            "skill source {Source}: fetch started; url={Url}; ref={Ref}; cache={Cache}; timeout={Timeout}s",
            Id, RedactUrl(_url), refLabel, _cacheDir, GitTimeoutSeconds);
        var before = await RevParseHeadAsync(cancellationToken);

        var refSpec = string.IsNullOrEmpty(_gitRef) ? "HEAD" : _gitRef;
        if (!await RunGitAsync(["-C", _cacheDir, "fetch", "--depth", "1", "origin", refSpec], cancellationToken))
        {
            return false;
        }

        if (!await RunGitAsync(["-C", _cacheDir, "reset", "--hard", "FETCH_HEAD"], cancellationToken))
        {
            return false;
        }

        var after = await RevParseHeadAsync(cancellationToken);
        if (before is not null && before == after)
        {
            _logger.LogInformation(
                "skill source {Source}: refresh completed; checkout unchanged; commit={Commit}; duration={Duration}ms",
                Id, after, stopwatch.ElapsedMilliseconds);
        }
        else
        {
            _logger.LogInformation(
                "skill source {Source}: refresh completed; checkout updated; {Before} -> {After}; duration={Duration}ms",
                Id, before ?? "unknown", after ?? "unknown", stopwatch.ElapsedMilliseconds);
        }

        return true;
    }

    /// <summary>The checked-out commit of the cache, or null when git cannot tell (no checkout, git missing).</summary>
    private async Task<string?> RevParseHeadAsync(CancellationToken cancellationToken)
    {
        var (ok, stdout) = await RunGitCapturingAsync(["-C", _cacheDir, "rev-parse", "HEAD"], cancellationToken, quiet: true);
        var sha = stdout.Trim();
        return ok && sha.Length > 0 ? sha : null;
    }

    private async Task<bool> RunGitAsync(List<string> args, CancellationToken cancellationToken)
        => (await RunGitCapturingAsync(args, cancellationToken, quiet: false)).Ok;

    /// <summary>
    /// Runs one git command. The token travels as an <c>http.extraHeader</c> config argument —
    /// never on the URL (which would leak into logs and shell history) — and the logged argument
    /// list has the URL's userinfo redacted. <paramref name="quiet"/> suppresses failure logging
    /// for probes whose failure is an expected answer (rev-parse on an empty cache).
    /// </summary>
    private async Task<(bool Ok, string Stdout)> RunGitCapturingAsync(List<string> args, CancellationToken cancellationToken, bool quiet)
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
                if (!quiet)
                {
                    _logger.LogWarning("skill source {Source}: failed to start git; source skipped", Id);
                }
                return (false, "");
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            if (!quiet)
            {
                _logger.LogWarning("skill source {Source}: git is not installed or not on PATH; source skipped ({Message})", Id, ex.Message);
            }
            return (false, "");
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(GitTimeoutSeconds));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            if (!quiet)
            {
                _logger.LogWarning(
                    "skill source {Source}: git {Args} timed out after {Timeout}s",
                    Id, DescribeArgs(args), GitTimeoutSeconds);
            }
            return (false, "");
        }

        if (process.ExitCode != 0)
        {
            if (!quiet)
            {
                var stderr = (await stderrTask).Trim();
                _logger.LogWarning(
                    "skill source {Source}: git {Args} exited {Code}: {Stderr}",
                    Id, DescribeArgs(args), process.ExitCode, Truncate(stderr));
            }
            return (false, "");
        }

        return (true, await stdoutTask);
    }

    /// <summary>The argument list for a log line, with the clone URL redacted.</summary>
    private string DescribeArgs(List<string> args)
        => string.Join(' ', args.Select(a => a == _url ? RedactUrl(a) : a));

    /// <summary>
    /// Removes userinfo (<c>user:token@</c>) from a URL before it is logged. The token option is
    /// the supported way to authenticate, but a credential pasted into the URL must not leak
    /// through the log either.
    /// </summary>
    internal static string RedactUrl(string url)
    {
        var schemeEnd = url.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0)
        {
            return url; // scp-style or local path: no userinfo syntax to strip
        }

        var authorityStart = schemeEnd + 3;
        var authorityEnd = url.IndexOf('/', authorityStart);
        var authority = authorityEnd < 0 ? url[authorityStart..] : url[authorityStart..authorityEnd];
        var at = authority.LastIndexOf('@');
        return at < 0 ? url : url[..authorityStart] + "***@" + url[(authorityStart + at + 1)..];
    }

    private static string Truncate(string value)
        => value.Length <= 500 ? value : value[..500] + "…";

    /// <summary>Keeps the cache folder name filesystem-safe regardless of what the config id contains.</summary>
    private static string Sanitize(string id)
        => string.Concat(id.Select(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '-'));
}
