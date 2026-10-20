using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Reload;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// The skills startup pass runs before the MCP server's hosted service starts, so the client's
/// first request waits for it. It must therefore never wait on the network: a git source serves
/// its cached checkout (or nothing, on the very first start) and fetches in the background.
/// Hosts that drop a server which is not ready when a session is created (the GitHub Copilot app)
/// otherwise lose the whole orchestrator for that session to a skills fetch.
/// </summary>
public sealed class SkillsStartupTests : IDisposable
{
    private const string SourceId = "TEAM";

    private readonly string _root = Directory.CreateDirectory(
        Path.Combine(Path.GetTempPath(), $"skills-startup-{Guid.NewGuid():N}")).FullName;
    private readonly CollectingLogger _log = new();

    private string Origin => Path.Combine(_root, "origin");
    private string CacheRoot => Path.Combine(_root, "cache");
    private string OriginUrl => new Uri(Origin).AbsoluteUri;
    private string CacheDir => Path.Combine(CacheRoot, SourceId);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    [Fact]
    public async Task Startup_serves_the_cached_checkout_without_waiting_for_a_hanging_fetch()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        await PrimeCacheAsync();
        using var server = new SilentHttpServer();
        GitIn(CacheDir, "remote", "set-url", "origin", server.Url); // the refresh now hangs until killed
        var registry = new SkillRegistry();
        var service = NewService(registry);

        var startup = Stopwatch.StartNew();
        await service.ApplyAsync(GitOptions(), SkillRebuildTrigger.Startup, CancellationToken.None);
        startup.Stop();

        // The fetch would block for GitSkillSource.GitTimeoutSeconds (60s); startup must not.
        Assert.True(startup.Elapsed < TimeSpan.FromSeconds(10), $"startup took {startup.Elapsed}");
        Assert.Equal("one", Assert.Single(registry.Current.Skills).Name);
        Assert.Contains(_log.Entries, e => e.Message.Contains("TEAM: 1 skill(s) loaded from the local copy"));
        Assert.Contains(_log.Entries, e => e.Message.Contains("refreshing TEAM in the background"));
        Assert.NotNull(service.StartupRefreshTask);
        Assert.True(server.WaitForConnection(TimeSpan.FromSeconds(30)), "the background refresh never contacted the origin");

        // Shutdown cancels the hung refresh instead of leaving it for the 60s git timeout.
        var shutdown = Stopwatch.StartNew();
        service.Dispose();
        await service.StartupRefreshTask!.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(shutdown.Elapsed < TimeSpan.FromSeconds(20), $"refresh outlived shutdown by {shutdown.Elapsed}");
        Assert.Equal("one", Assert.Single(registry.Current.Skills).Name);
    }

    [Fact]
    public async Task The_background_refresh_swaps_in_skills_added_since_the_cache_was_written()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        await PrimeCacheAsync();
        AddSkillToOrigin("two");
        var registry = new SkillRegistry();
        using var service = NewService(registry);

        await service.ApplyAsync(GitOptions(), SkillRebuildTrigger.Startup, CancellationToken.None);

        Assert.Equal("one", Assert.Single(registry.Current.Skills).Name); // the cache, as it was
        await service.StartupRefreshTask!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal(["one", "two"], registry.Current.Skills.Select(s => s.Name).Order().ToArray());
        var refreshed = Assert.Single(_log.Entries, e => e.Message.Contains("trigger=startup-refresh"));
        Assert.Contains("TEAM=2 fresh content", refreshed.Message);
        Assert.Contains("1 added", refreshed.Message);
    }

    [Fact]
    public async Task The_first_start_without_a_cache_does_not_wait_for_the_clone()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var registry = new SkillRegistry();
        using var service = NewService(registry);

        await service.ApplyAsync(GitOptions(), SkillRebuildTrigger.Startup, CancellationToken.None);

        Assert.Empty(registry.Current.Skills);
        Assert.False(Directory.Exists(Path.Combine(CacheDir, ".git")), "startup must not have cloned");
        Assert.Contains(_log.Entries, e => e.Message.Contains("TEAM: 0 skill(s) loaded from nothing yet (first fetch follows in the background)"));
        await service.StartupRefreshTask!.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.Equal("one", Assert.Single(registry.Current.Skills).Name);
    }

    [Fact]
    public async Task A_directory_only_startup_loads_in_place_and_starts_no_background_refresh()
    {
        var dir = Directory.CreateDirectory(Path.Combine(_root, "local", "a")).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), "---\nname: a\ndescription: a.\n---\nbody");
        var registry = new SkillRegistry();
        using var service = NewService(registry);

        await service.ApplyAsync(new SkillsOptions
        {
            Sources = { new SkillSourceOptions { Id = "local", Type = "directory", Path = Path.Combine(_root, "local") } },
        }, SkillRebuildTrigger.Startup, CancellationToken.None);

        Assert.Equal("a", Assert.Single(registry.Current.Skills).Name);
        Assert.Null(service.StartupRefreshTask);
        Assert.Contains(_log.Entries, e => e.Message.Contains("local: 1 skill(s) loaded from fresh content"));
    }

    [Fact]
    public async Task A_config_change_still_fetches_before_it_returns()
    {
        if (!GitAvailable()) return;
        SeedOrigin("one");
        var registry = new SkillRegistry();
        using var service = NewService(registry);

        await ((ISkillsReloadSink)service).ApplyAsync(GitOptions(), CancellationToken.None);

        Assert.Equal("one", Assert.Single(registry.Current.Skills).Name);
        Assert.Null(service.StartupRefreshTask);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Central_modes_initial_load_takes_the_startup_path_and_later_reloads_do_not(bool startup)
    {
        var sink = new RecordingSkillsSink();
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors([], NullLogger.Instance));
        var next = new ReloadedConfig(CapabilityCatalog.FromDescriptors([], NullLogger.Instance), []);
        var reloader = new ConfigReloader(new FixedSource(next), registry, new SpyLifecycle(), NullLogger.Instance, sink);

        await reloader.ReloadAsync(CancellationToken.None, startup ? ReloadReason.Startup : ReloadReason.Change);

        Assert.Equal([startup ? "startup" : "change"], sink.Calls);
    }

    private SkillsReloadService NewService(SkillRegistry registry) => new(
        registry,
        new CapabilityRegistry(CapabilityCatalog.FromDescriptors([], NullLogger.Instance)),
        new WrappingLogger<SkillsReloadService>(_log),
        CacheRoot);

    private SkillsOptions GitOptions() => new()
    {
        Sources = { new SkillSourceOptions { Id = SourceId, Type = "git", Url = OriginUrl, Ref = "main" } },
    };

    /// <summary>Clones the origin into the cache, as an earlier orchestrator run would have.</summary>
    private async Task PrimeCacheAsync()
    {
        var primer = new GitSkillSource(SourceId, OriginUrl, "main", null, CacheRoot, NullLogger.Instance);
        var load = await primer.LoadAsync(CancellationToken.None);
        Assert.Equal(SkillSourceFreshness.Fresh, load.Freshness);
    }

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

    private void SeedOrigin(string skill)
    {
        Directory.CreateDirectory(Origin);
        GitIn(Origin, "init", "-q", "-b", "main");
        GitIn(Origin, "config", "user.email", "test@example.com");
        GitIn(Origin, "config", "user.name", "Test");
        AddSkillToOrigin(skill);
    }

    private void AddSkillToOrigin(string skill)
    {
        var dir = Directory.CreateDirectory(Path.Combine(Origin, skill)).FullName;
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), $"---\nname: {skill}\ndescription: Skill {skill}.\n---\nbody");
        GitIn(Origin, "add", ".");
        GitIn(Origin, "commit", "-q", "-m", $"add {skill}");
    }

    private static void GitIn(string workingDirectory, params string[] args)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = workingDirectory, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        foreach (var a in args) start.ArgumentList.Add(a);
        using var p = Process.Start(start)!;
        p.StandardOutput.ReadToEnd();
        var stderr = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
    }

    /// <summary>Accepts HTTP connections and never answers, like an origin that hangs mid-fetch.</summary>
    private sealed class SilentHttpServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = new();
        private readonly ManualResetEventSlim _connected = new();

        public SilentHttpServer()
        {
            _listener.Start();
            _ = AcceptAsync();
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/skills.git";

        public bool WaitForConnection(TimeSpan timeout) => _connected.Wait(timeout);

        private async Task AcceptAsync()
        {
            try
            {
                while (true)
                {
                    var client = await _listener.AcceptTcpClientAsync();
                    lock (_clients) { _clients.Add(client); }
                    _connected.Set();
                }
            }
            catch (Exception) { /* listener stopped */ }
        }

        public void Dispose()
        {
            _listener.Stop();
            lock (_clients)
            {
                foreach (var c in _clients) c.Dispose();
            }
        }
    }

    private sealed class WrappingLogger<T>(ILogger inner) : ILogger<T>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            inner.Log(logLevel, eventId, state, exception, formatter);
    }

    private sealed class FixedSource(ReloadedConfig next) : IConfigSource
    {
        public string Description => "fixed-source";
        public Task<ReloadedConfig?> TryLoadAsync(CancellationToken cancellationToken) => Task.FromResult<ReloadedConfig?>(next);
    }

    private sealed class RecordingSkillsSink : ISkillsReloadSink
    {
        public List<string> Calls { get; } = new();

        public Task<SkillDiff> ApplyAsync(SkillsOptions? options, CancellationToken cancellationToken)
        {
            Calls.Add("change");
            return Task.FromResult(SkillDiff.Empty);
        }

        public Task<SkillDiff> ApplyStartupAsync(SkillsOptions? options, CancellationToken cancellationToken)
        {
            Calls.Add("startup");
            return Task.FromResult(SkillDiff.Empty);
        }
    }
}
