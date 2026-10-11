using System.Diagnostics;
using System.Text;
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
/// <para>The cache folder is shared by every orchestrator process on the machine (hosts routinely
/// run several at once, and restart them all when the config changes), so the sync holds a lock
/// file next to the folder: the first instance clones or fetches, the others wait and then scan
/// the result instead of racing into the same directory.</para>
/// <para>Git gets its own (closed) stdin rather than inheriting the orchestrator's: that pipe
/// belongs to the MCP transport, and Git for Windows blocks at startup on an inherited pipe
/// another process is reading.</para>
/// <para>Git runs non-interactively: credential prompts are disabled so a missing credential fails
/// in a second with a clear error instead of hanging until the timeout kills it. Without a
/// <c>token</c> the user's stored credentials (a credential helper's cache) are used. With a
/// <c>token</c> it is sent as HTTP basic auth (the way GitHub, GitLab and Azure DevOps document
/// token access over HTTPS; GitHub's git endpoints reject <c>Bearer</c>) and credential helpers are
/// switched off for that command: a rejected token must fail fast, not hand the request to a
/// helper that may prompt, or erase the user's stored credential because the server said 401.</para>
/// <para>Every stage is logged so a refresh can be followed without guessing: clone or fetch
/// started (ref, cache path, timeout), completed (duration, commit; for a refresh whether the
/// checkout changed), and on failure whether a cached checkout keeps serving. The URL is logged
/// with any userinfo removed and the token never appears anywhere.</para>
/// </remarks>
internal sealed class GitSkillSource : ISkillSource
{
    /// <summary>Seconds allowed for a clone or fetch before it is killed.</summary>
    internal const int GitTimeoutSeconds = 60;

    /// <summary>Seconds allowed for a local probe such as <c>rev-parse</c>, which never touches the network.</summary>
    internal const int ProbeTimeoutSeconds = 10;

    /// <summary>
    /// Seconds a second instance waits for the lock: the holder's clone can take up to
    /// <see cref="GitTimeoutSeconds"/> plus its probes, so wait a little longer than that.
    /// </summary>
    internal const int LockWaitSeconds = GitTimeoutSeconds + 15;

    private static readonly TimeSpan LockPollInterval = TimeSpan.FromMilliseconds(250);

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

    /// <summary>The lock file guarding <see cref="CacheDir"/> across processes (a sibling, since a clone target must be empty).</summary>
    internal string LockPath => _cacheDir + ".lock";

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

    /// <summary>What the log says about credentials: the token, or whatever git finds on its own.</summary>
    private string AuthLabel => string.IsNullOrEmpty(_token) ? "none (git credential helper)" : "token";

    private bool HasCheckout() => Directory.Exists(Path.Combine(_cacheDir, ".git"));

    /// <summary>
    /// Under the cache lock: clones on first use, otherwise fetches and hard-resets to the remote
    /// state. The clone-or-fetch decision is made after the lock is held, so an instance that
    /// waited for a sibling's clone sees the finished checkout and fetches (a cheap no-op).
    /// </summary>
    private async Task<bool> SyncAsync(CancellationToken cancellationToken)
    {
        using var cacheLock = await AcquireLockAsync(cancellationToken);
        if (cacheLock is null)
        {
            return false;
        }

        var refLabel = string.IsNullOrEmpty(_gitRef) ? "default branch" : _gitRef;
        var stopwatch = Stopwatch.StartNew();

        if (!HasCheckout())
        {
            PrepareEmptyCacheDir();
            _logger.LogInformation(
                "skill source {Source}: clone started; url={Url}; ref={Ref}; auth={Auth}; cache={Cache}; timeout={Timeout}s",
                Id, RedactUrl(_url), refLabel, AuthLabel, _cacheDir, GitTimeoutSeconds);

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
            "skill source {Source}: fetch started; url={Url}; ref={Ref}; auth={Auth}; cache={Cache}; timeout={Timeout}s",
            Id, RedactUrl(_url), refLabel, AuthLabel, _cacheDir, GitTimeoutSeconds);
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

    /// <summary>
    /// A clone target must not exist or must be empty. A folder without a <c>.git</c> is
    /// leftover from an interrupted clone (or a sibling's failed attempt before locking existed)
    /// and is ours to discard.
    /// </summary>
    private void PrepareEmptyCacheDir()
    {
        if (Directory.Exists(_cacheDir) && Directory.EnumerateFileSystemEntries(_cacheDir).Any())
        {
            _logger.LogWarning(
                "skill source {Source}: cache folder {Cache} has content but no checkout; clearing it before cloning",
                Id, _cacheDir);
            Directory.Delete(_cacheDir, recursive: true);
        }

        Directory.CreateDirectory(_cacheDir);
    }

    /// <summary>
    /// Takes the cross-process lock for the cache folder, waiting up to <see cref="LockWaitSeconds"/>
    /// for a sibling instance to finish its sync. Returns <c>null</c> when the lock could not be
    /// taken in time; the caller then falls back to whatever checkout exists.
    /// </summary>
    private async Task<FileStream?> AcquireLockAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(LockPath)!);
        var waited = Stopwatch.StartNew();
        var announced = false;
        while (true)
        {
            try
            {
                // FileShare.None is a real exclusive lock on Windows (sharing violation) and on
                // Unix (.NET maps it to flock). DeleteOnClose keeps the folder tidy; a waiter
                // re-opens by path each attempt, so a vanished file is simply recreated.
                return new FileStream(LockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, bufferSize: 1, FileOptions.DeleteOnClose);
            }
            catch (IOException) when (waited.Elapsed < TimeSpan.FromSeconds(LockWaitSeconds))
            {
                if (!announced)
                {
                    announced = true;
                    _logger.LogInformation(
                        "skill source {Source}: another orchestrator instance is syncing {Cache}; waiting for it (up to {Seconds}s)",
                        Id, _cacheDir, LockWaitSeconds);
                }

                await Task.Delay(LockPollInterval, cancellationToken);
            }
            catch (IOException)
            {
                _logger.LogWarning(
                    "skill source {Source}: could not take the cache lock {Lock} within {Seconds}s; another instance still holds it",
                    Id, LockPath, LockWaitSeconds);
                return null;
            }
        }
    }

    /// <summary>The checked-out commit of the cache, or null when git cannot tell (no checkout, git missing).</summary>
    private async Task<string?> RevParseHeadAsync(CancellationToken cancellationToken)
    {
        var (ok, stdout) = await RunGitCapturingAsync(["-C", _cacheDir, "rev-parse", "HEAD"], cancellationToken, quiet: true, ProbeTimeoutSeconds);
        var sha = stdout.Trim();
        return ok && sha.Length > 0 ? sha : null;
    }

    private async Task<bool> RunGitAsync(List<string> args, CancellationToken cancellationToken)
        => (await RunGitCapturingAsync(args, cancellationToken, quiet: false, GitTimeoutSeconds)).Ok;

    /// <summary>
    /// Runs one git command. The token travels as an <c>http.extraHeader</c> config argument —
    /// never on the URL (which would leak into logs and shell history) — and the logged argument
    /// list has the URL's userinfo redacted. <paramref name="quiet"/> suppresses failure logging
    /// for probes whose failure is an expected answer (rev-parse on an empty cache).
    /// </summary>
    private async Task<(bool Ok, string Stdout)> RunGitCapturingAsync(
        List<string> args, CancellationToken cancellationToken, bool quiet, int timeoutSeconds)
    {
        var start = CreateStartInfo(_token, args);
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

            process.StandardInput.Close();
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
        timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone, or not ours to kill */ }
            if (!quiet)
            {
                _logger.LogWarning(
                    "skill source {Source}: git {Args} timed out after {Timeout}s and was killed. A git command that " +
                    "neither finishes nor fails is usually waiting on a credential helper (a login window or prompt " +
                    "nobody sees); check that `git ls-remote <url>` completes in a terminal without asking, or set the " +
                    "source's \"token\"",
                    Id, DescribeArgs(args), timeoutSeconds);
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
                if (LooksLikeAuthFailure(stderr) && !string.IsNullOrEmpty(_token))
                {
                    _logger.LogWarning(
                        "skill source {Source}: the remote {Url} rejected the configured token. Check that the " +
                        "variable behind \"token\" holds a current token with read access to this repository " +
                        "(GitHub classic token: scope repo; fine-grained token: Contents read-only)",
                        Id, RedactUrl(_url));
                }
                else if (LooksLikeAuthFailure(stderr))
                {
                    _logger.LogWarning(
                        "skill source {Source}: git could not authenticate to {Url}. The orchestrator runs git without " +
                        "credential prompts, so a private repo needs either a stored credential (git credential helper) " +
                        "or the source's \"token\" setting, e.g. \"token\": \"${{SKILLS_GIT_TOKEN}}\"",
                        Id, RedactUrl(_url));
                }
            }
            return (false, "");
        }

        return (true, await stdoutTask);
    }

    /// <summary>
    /// The start info for one git command: all three standard streams redirected, prompts off,
    /// auth arguments, then the command. Stdin is redirected on purpose and closed right after
    /// start: the orchestrator is a stdio MCP server whose own stdin is a pipe the transport is
    /// always reading, and Git for Windows hangs at startup when it inherits such a pipe (its
    /// start-up probe of stdin blocks behind the pending read), so the clone never even began.
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string? token, IEnumerable<string> args)
    {
        var start = new ProcessStartInfo
        {
            FileName = "git",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        ConfigureNonInteractive(start);
        AddAuthArguments(start, token);
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        return start;
    }

    /// <summary>
    /// Disables every interactive credential path: git's own terminal prompt, Git Credential
    /// Manager's UI (both its environment switch and its git-config switch), and askpass helpers.
    /// A credential that is already stored is still used.
    /// </summary>
    internal static void ConfigureNonInteractive(ProcessStartInfo start)
    {
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.Environment["GCM_INTERACTIVE"] = "never";
        start.Environment["GIT_ASKPASS"] = "";
        start.Environment["SSH_ASKPASS"] = "";
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("credential.interactive=never");
    }

    /// <summary>
    /// With a token: send it as basic auth via <c>http.extraHeader</c> (never on the URL, which
    /// would leak into logs) and turn credential helpers off for this command, so the token is the
    /// only credential tried. Without a token: nothing is added and stored credentials apply.
    /// </summary>
    internal static void AddAuthArguments(ProcessStartInfo start, string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return;
        }

        start.ArgumentList.Add("-c");
        start.ArgumentList.Add("credential.helper=");
        start.ArgumentList.Add("-c");
        start.ArgumentList.Add($"http.extraHeader={AuthorizationHeader(token)}");
    }

    /// <summary>
    /// The header value for a token: HTTP basic auth with the placeholder user name
    /// <c>x-access-token</c>, which GitHub (PATs and app tokens), GitLab and Azure DevOps all accept
    /// because the user name is ignored when the password is a token.
    /// </summary>
    internal static string AuthorizationHeader(string token)
        => "Authorization: Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("x-access-token:" + token));

    /// <summary>Recognizes git's ways of saying the remote refused or required credentials.</summary>
    internal static bool LooksLikeAuthFailure(string stderr) =>
        stderr.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("could not read Username", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("could not read Password", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("terminal prompts disabled", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Repository not found", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("HTTP 401", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("HTTP 403", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("Invalid username or token", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("unable to get password", StringComparison.OrdinalIgnoreCase)
        || stderr.Contains("could not read Password", StringComparison.OrdinalIgnoreCase);

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
