using System.Diagnostics;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Drives <see cref="GitSkillSource"/> against a throwaway local repository (served over
/// <c>file://</c>, which makes git honor <c>--depth</c> like a remote) and checks that each stage
/// of a clone/refresh is told apart in the log: fresh clone, unchanged refresh, updated checkout,
/// and failure with and without a cached checkout. Skipped when git is not on PATH.
/// </summary>
public sealed class GitSkillSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"skills-git-{Guid.NewGuid():N}")).FullName;

    private string Origin => Path.Combine(_root, "origin");
    private string CacheRoot => Path.Combine(_root, "cache");
    private string OriginUrl => new Uri(Origin).AbsoluteUri;

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task First_load_clones_and_logs_start_and_completion_with_the_commit()
    {
        if (!GitAvailable()) return;
        var head = SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log, pollSeconds: 300);

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Fresh, load.Freshness);
        Assert.Equal("one", Assert.Single(load.Skills).Name);
        var started = Assert.Single(log.Entries, e => e.Message.Contains("clone started"));
        Assert.Contains("ref=main", started.Message);
        Assert.Contains($"timeout={GitSkillSource.GitTimeoutSeconds}s", started.Message);
        Assert.Contains(source.CacheDir, started.Message);
        var completed = Assert.Single(log.Entries, e => e.Message.Contains("clone completed"));
        Assert.Contains($"commit={head}", completed.Message);
        Assert.Contains("duration=", completed.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("fetch started"));
    }

    [Fact]
    public async Task Refresh_without_remote_changes_reports_the_checkout_unchanged()
    {
        if (!GitAvailable()) return;
        var head = SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log);
        await source.LoadAsync(CancellationToken.None);

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Fresh, load.Freshness);
        Assert.Single(log.Entries, e => e.Message.Contains("fetch started"));
        var completed = Assert.Single(log.Entries, e => e.Message.Contains("refresh completed"));
        Assert.Contains("checkout unchanged", completed.Message);
        Assert.Contains($"commit={head}", completed.Message);
    }

    [Fact]
    public async Task Refresh_after_a_remote_commit_reports_old_and_new_commits()
    {
        if (!GitAvailable()) return;
        var first = SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log);
        await source.LoadAsync(CancellationToken.None);
        var second = AddSkillToOrigin("two");

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(2, load.Skills.Count); // same source, now one more skill
        var completed = Assert.Single(log.Entries, e => e.Message.Contains("refresh completed"));
        Assert.Contains("checkout updated", completed.Message);
        Assert.Contains($"{first} -> {second}", completed.Message);
    }

    [Fact]
    public async Task Failed_refresh_serves_the_cached_checkout_and_says_so()
    {
        if (!GitAvailable()) return;
        var head = SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log, pollSeconds: 120);
        await source.LoadAsync(CancellationToken.None);
        Directory.Move(Origin, Origin + "-gone"); // the remote disappears

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Cached, load.Freshness);
        Assert.Equal("one", Assert.Single(load.Skills).Name);
        var warning = Assert.Single(log.Entries, e => e.Message.Contains("serving the cached checkout"));
        Assert.Equal(LogLevel.Warning, warning.Level);
        Assert.Contains($"commit {head}", warning.Message);
        Assert.Contains("next refresh in 120s", warning.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("refresh completed"));
    }

    [Fact]
    public async Task Failed_clone_without_a_cache_yields_nothing_and_says_so()
    {
        if (!GitAvailable()) return;
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", new Uri(Path.Combine(_root, "missing")).AbsoluteUri, null, null, CacheRoot, log, pollSeconds: 60);

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Unavailable, load.Freshness);
        Assert.Empty(load.Skills);
        var warning = Assert.Single(log.Entries, e => e.Message.Contains("no usable checkout"));
        Assert.Contains("next refresh in 60s", warning.Message);
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("clone completed"));
    }

    [Fact]
    public async Task Concurrent_instances_share_one_clone_through_the_cache_lock()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var logA = new CollectingLogger();
        var logB = new CollectingLogger();
        // Two sources with the same id and cache root stand in for two orchestrator processes
        // (hosts start several at once and restart them all on a config change).
        var a = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, logA);
        var b = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, logB);

        var loads = await Task.WhenAll(a.LoadAsync(CancellationToken.None), b.LoadAsync(CancellationToken.None));

        Assert.All(loads, l => Assert.Equal(SkillSourceFreshness.Fresh, l.Freshness));
        Assert.All(loads, l => Assert.Equal("one", Assert.Single(l.Skills).Name));
        var all = logA.Entries.Concat(logB.Entries).ToList();
        Assert.Single(all, e => e.Message.Contains("clone started"));
        Assert.Single(all, e => e.Message.Contains("fetch started")); // the waiter found the finished checkout
        Assert.DoesNotContain(all, e => e.Message.Contains("already exists"));
        Assert.DoesNotContain(all, e => e.Level == LogLevel.Warning);
        Assert.False(File.Exists(a.LockPath)); // DeleteOnClose
    }

    [Fact]
    public async Task A_cache_folder_without_a_checkout_is_cleared_before_cloning()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log);
        Directory.CreateDirectory(source.CacheDir);
        File.WriteAllText(Path.Combine(source.CacheDir, "leftover.txt"), "from an interrupted clone");

        var load = await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Fresh, load.Freshness);
        Assert.Single(load.Skills);
        Assert.Contains(log.Entries, e => e.Message.Contains("has content but no checkout; clearing it"));
        Assert.False(File.Exists(Path.Combine(source.CacheDir, "leftover.txt")));
    }

    [Fact]
    public void Git_runs_without_credential_prompts()
    {
        var start = new ProcessStartInfo("git");

        GitSkillSource.ConfigureNonInteractive(start);

        Assert.Equal("0", start.Environment["GIT_TERMINAL_PROMPT"]);
        Assert.Equal("never", start.Environment["GCM_INTERACTIVE"]);
        Assert.Equal("", start.Environment["GIT_ASKPASS"]);
        Assert.Equal(["-c", "credential.interactive=never"], start.ArgumentList);
    }

    [Fact]
    public void Git_gets_its_own_stdin_instead_of_inheriting_the_mcp_transport_pipe()
    {
        // Git for Windows hangs at startup when it inherits a stdin pipe another process is
        // reading, which is what a stdio MCP server's stdin is; every clone then hit the timeout.
        var start = GitSkillSource.CreateStartInfo(null, ["clone", "x"]);

        Assert.True(start.RedirectStandardInput);
        Assert.True(start.RedirectStandardOutput);
        Assert.True(start.RedirectStandardError);
        Assert.Equal("git", start.FileName);
        Assert.Equal(["-c", "credential.interactive=never", "clone", "x"], start.ArgumentList);
    }

    [Fact]
    public void A_token_is_sent_as_basic_auth_and_turns_credential_helpers_off()
    {
        var start = new ProcessStartInfo("git");

        GitSkillSource.AddAuthArguments(start, "ghp_secret");

        // GitHub's git endpoints answer 401 to "Bearer <token>" even when the token is valid; basic
        // auth with the token as password is what every forge documents.
        Assert.Equal(
            ["-c", "credential.helper=", "-c", "http.extraHeader=Authorization: Basic " + Base64("x-access-token:ghp_secret")],
            start.ArgumentList);
    }

    [Fact]
    public void Without_a_token_stored_credentials_stay_in_play()
    {
        var start = new ProcessStartInfo("git");

        GitSkillSource.AddAuthArguments(start, null);
        GitSkillSource.AddAuthArguments(start, "");

        Assert.Empty(start.ArgumentList);
    }

    [Fact]
    public async Task Clone_with_a_token_works_and_logs_the_auth_mode_without_the_token()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", "ghp_secret", CacheRoot, log);

        var load = await source.LoadAsync(CancellationToken.None);
        await source.LoadAsync(CancellationToken.None);

        Assert.Equal(SkillSourceFreshness.Fresh, load.Freshness);
        Assert.Contains(log.Entries, e => e.Message.Contains("clone started") && e.Message.Contains("auth=token"));
        Assert.Contains(log.Entries, e => e.Message.Contains("fetch started") && e.Message.Contains("auth=token"));
        Assert.DoesNotContain(log.Entries, e => e.Message.Contains("ghp_secret") || e.Message.Contains(Base64("x-access-token:ghp_secret")));
    }

    [Fact]
    public async Task Clone_without_a_token_logs_that_git_credentials_apply()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var log = new CollectingLogger();
        var source = new GitSkillSource("team", OriginUrl, "main", null, CacheRoot, log);

        await source.LoadAsync(CancellationToken.None);

        Assert.Contains(log.Entries, e => e.Message.Contains("clone started") && e.Message.Contains("auth=none (git credential helper)"));
    }

    private static string Base64(string text) => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void Authentication_failures_are_recognized_in_git_stderr()
    {
        Assert.True(GitSkillSource.LooksLikeAuthFailure("fatal: could not read Username for 'https://github.com': terminal prompts disabled"));
        Assert.True(GitSkillSource.LooksLikeAuthFailure("remote: Repository not found.\nfatal: repository 'https://github.com/org/private.git/' not found"));
        Assert.True(GitSkillSource.LooksLikeAuthFailure("fatal: Authentication failed for 'https://github.com/org/private.git/'"));
        // What git says when a token in http.extraHeader is rejected and its (closed) stdin cannot supply a password.
        Assert.True(GitSkillSource.LooksLikeAuthFailure("Cloning into 'x'..." + Environment.NewLine + "fatal: unable to get password from user"));
        Assert.False(GitSkillSource.LooksLikeAuthFailure("fatal: destination path 'x' already exists and is not an empty directory."));
        Assert.False(GitSkillSource.LooksLikeAuthFailure("fatal: unable to access 'https://github.com/': Could not resolve host: github.com"));
    }

    [Fact]
    public void Logged_url_has_credentials_in_userinfo_removed()
    {
        Assert.Equal("https://***@github.com/org/skills.git", GitSkillSource.RedactUrl("https://user:ghp_secret@github.com/org/skills.git"));
        Assert.Equal("https://github.com/org/skills.git", GitSkillSource.RedactUrl("https://github.com/org/skills.git"));
        Assert.Equal("git@github.com:org/skills.git", GitSkillSource.RedactUrl("git@github.com:org/skills.git"));
    }

    // ----- helpers --------------------------------------------------------------------------------

    private static bool GitAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("git", "--version") { RedirectStandardOutput = true, UseShellExecute = false });
            p!.WaitForExit();
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Creates the origin repo with one skill on <c>main</c>; returns HEAD.</summary>
    private string SeedOrigin(string skill)
    {
        Directory.CreateDirectory(Origin);
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "test@example.com");
        Git("config", "user.name", "Test");
        return AddSkillToOrigin(skill);
    }

    private string AddSkillToOrigin(string skill)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Origin, skill)).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {skill}\ndescription: Skill {skill}.\n---\nbody");
        Git("add", ".");
        Git("commit", "-q", "-m", $"add {skill}");
        return Git("rev-parse", "HEAD").Trim();
    }

    private string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = Origin, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        var stdout = p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout;
    }
}
