using System.Net;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Reload;
using McpOrchestrator.Tools;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Covers centrally managed config mode against a local <see cref="HttpListener"/> test server
/// (no external network): source selection, the ETag/304 short-circuit, auth handling, startup
/// cache fallbacks, poll-failure backoff, and the central-only validation rules.
/// </summary>
public sealed class CentralConfigTests
{
    private const string AlphaConfig = """
        { "capabilities": [ { "name": "alpha", "summary": "A.", "command": "cmd-a" } ] }
        """;

    private const string AlphaBetaConfig = """
        {
          "capabilities": [
            { "name": "alpha", "summary": "A.", "command": "cmd-a" },
            { "name": "beta",  "summary": "B.", "command": "cmd-b" }
          ]
        }
        """;

    // ----- source selection ----------------------------------------------------------------------

    [Fact]
    public void Url_env_var_enters_central_mode_and_records_the_ignored_local_path()
    {
        WithEnv(() =>
            {
                var options = CentralConfigOptions.FromEnvironment();

                Assert.NotNull(options);
                Assert.Equal("https://example.com/team.json", options!.Url.ToString());
                Assert.Equal(@"C:\local\orchestrator.config.json", options.IgnoredLocalConfigPath);
            },
            ("MCP_ORCHESTRATOR_CONFIG_URL", "https://example.com/team.json"),
            ("MCP_ORCHESTRATOR_CONFIG", @"C:\local\orchestrator.config.json"));
    }

    [Fact]
    public void No_url_env_var_stays_in_local_file_mode()
    {
        WithEnv(
            () => Assert.Null(CentralConfigOptions.FromEnvironment()),
            ("MCP_ORCHESTRATOR_CONFIG_URL", null));
    }

    [Theory]
    [InlineData("http://example.com/team.json", false)] // plain http on a real host: rejected
    [InlineData("http://127.0.0.1:5000/team.json", true)] // loopback http: allowed for local testing
    [InlineData("http://localhost:5000/team.json", true)]
    [InlineData("https://example.com/team.json", true)]
    public void Https_is_required_except_for_loopback(string url, bool valid)
    {
        WithEnv(() =>
        {
            if (valid)
            {
                Assert.NotNull(CentralConfigOptions.FromEnvironment());
            }
            else
            {
                Assert.Contains("https", Assert.Throws<ArgumentException>(
                    () => CentralConfigOptions.FromEnvironment()).Message);
            }
        }, ("MCP_ORCHESTRATOR_CONFIG_URL", url));
    }

    [Fact]
    public async Task Service_startup_warns_that_the_local_config_path_is_ignored()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, AlphaConfig, ETag: "\"v1\"");
        await InTempDirAsync(async dir =>
        {
            var log = new CollectingLogger();
            var options = Options(server.Url, ignoredLocalPath: @"C:\local\orchestrator.config.json");
            using var service = new CentralConfigService(
                options, EmptyRegistry(), new SpyLifecycle(), Logger(log),
                handler: null, cache: new CentralConfigCache(dir));

            await service.StartAsync(CancellationToken.None);

            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Warning && e.Message.Contains("IGNORED") && e.Message.Contains("orchestrator.config.json"));
        });
    }

    // ----- ETag / 304 flow -------------------------------------------------------------------------

    [Fact]
    public async Task Second_poll_sends_if_none_match_and_a_304_skips_the_pipeline()
    {
        using var server = new TestConfigServer();
        server.Handler = req => req.IfNoneMatch == "\"v1\""
            ? new(304)
            : new(200, AlphaConfig, ETag: "\"v1\"");

        await InTempDirAsync(async dir =>
        {
            var pipelineRuns = 0;
            using var trigger = new HttpConfigPollTrigger(
                Options(server.Url), new CentralConfigCache(dir), NullLogger.Instance);
            Task OnChanged()
            {
                Interlocked.Increment(ref pipelineRuns);
                return Task.CompletedTask;
            }

            Assert.Equal(HttpConfigPollTrigger.PollOutcome.Applied, await trigger.PollOnceAsync(OnChanged, CancellationToken.None));
            Assert.Equal(1, pipelineRuns);
            Assert.Null(server.Requests[0].IfNoneMatch);

            // Unchanged on the server: the conditional request comes back 304 and the pipeline
            // (parse/diff/apply) is never entered.
            Assert.Equal(HttpConfigPollTrigger.PollOutcome.Unchanged, await trigger.PollOnceAsync(OnChanged, CancellationToken.None));
            Assert.Equal(1, pipelineRuns);
            Assert.Equal("\"v1\"", server.Requests[1].IfNoneMatch);
        });
    }

    [Fact]
    public async Task Changed_server_config_applies_on_the_next_poll()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, AlphaConfig, ETag: "\"v1\"");

        await InTempDirAsync(async dir =>
        {
            var (trigger, reloader, registry, spy) = Wire(server.Url, dir);
            using var _ = trigger;

            await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None);
            Assert.Contains("alpha", await OrchestratorTool.ListCapabilities(registry, NullLogger<OrchestratorTool>.Instance));

            server.Handler = _ => new(200, AlphaBetaConfig, ETag: "\"v2\"");

            Assert.Equal(
                HttpConfigPollTrigger.PollOutcome.Applied,
                await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None));
            var listed = await OrchestratorTool.ListCapabilities(registry, NullLogger<OrchestratorTool>.Instance);
            Assert.Contains("alpha", listed);
            Assert.Contains("beta", listed);
            Assert.Empty(spy.Invalidated); // additions only — nothing to retire
        });
    }

    // ----- auth ------------------------------------------------------------------------------------

    [Fact]
    public async Task Auth_header_is_sent_verbatim_and_never_logged()
    {
        const string secret = "Bearer secret-token-xyz";
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, AlphaConfig, ETag: "\"v1\"");

        await InTempDirAsync(async dir =>
        {
            var log = new CollectingLogger();
            using var trigger = new HttpConfigPollTrigger(
                Options(server.Url, auth: secret), new CentralConfigCache(dir), log);

            await trigger.PollOnceAsync(onChanged: null, CancellationToken.None);
            Assert.Equal(secret, server.Requests[0].Authorization);

            // Force a failure so the error path logs too, then assert the secret never appears.
            server.Handler = _ => new(500);
            await trigger.PollOnceAsync(onChanged: null, CancellationToken.None);

            Assert.NotEmpty(log.Entries);
            Assert.DoesNotContain(log.Entries, e => e.Message.Contains("secret-token-xyz"));
        });
    }

    [Fact]
    public async Task Unauthorized_response_logs_an_actionable_message()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(401);

        await InTempDirAsync(async dir =>
        {
            var log = new CollectingLogger();
            using var trigger = new HttpConfigPollTrigger(
                Options(server.Url), new CentralConfigCache(dir), log);

            Assert.Equal(HttpConfigPollTrigger.PollOutcome.Failed, await trigger.PollOnceAsync(null, CancellationToken.None));
            Assert.Contains(log.Entries, e =>
                e.Level == LogLevel.Error && e.Message.Contains("authorization failed") && e.Message.Contains("MCP_ORCHESTRATOR_CONFIG_AUTH"));
        });
    }

    // ----- startup: cache fallback ------------------------------------------------------------------

    [Fact]
    public async Task Unreachable_url_with_a_matching_cache_starts_from_the_cached_copy()
    {
        await InTempDirAsync(async dir =>
        {
            var unreachable = "http://127.0.0.1:1/team.json"; // nothing listens on port 1
            var cache = new CentralConfigCache(dir);
            cache.Write(AlphaConfig, new CentralCacheMeta(unreachable, "\"v1\"", null, DateTimeOffset.UtcNow));

            var log = new CollectingLogger();
            var registry = EmptyRegistry();
            using var service = new CentralConfigService(
                Options(unreachable), registry, new SpyLifecycle(), Logger(log), handler: null, cache: cache);

            await service.StartAsync(CancellationToken.None);

            Assert.NotNull(registry.Find("alpha"));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("cached copy"));
        });
    }

    [Fact]
    public async Task Cache_recorded_for_a_different_url_counts_as_no_cache()
    {
        await InTempDirAsync(async dir =>
        {
            var cache = new CentralConfigCache(dir);
            cache.Write(AlphaConfig, new CentralCacheMeta("https://other.example/team.json", null, null, DateTimeOffset.UtcNow));

            using var service = new CentralConfigService(
                Options("http://127.0.0.1:1/team.json"), EmptyRegistry(), new SpyLifecycle(),
                Logger(new CollectingLogger()), handler: null, cache: cache);

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("no usable cached copy", ex.Message);
        });
    }

    [Fact]
    public async Task No_cache_and_unreachable_url_fails_startup_with_a_clear_error()
    {
        await InTempDirAsync(async dir =>
        {
            using var service = new CentralConfigService(
                Options("http://127.0.0.1:1/team.json"), EmptyRegistry(), new SpyLifecycle(),
                Logger(new CollectingLogger()), handler: null, cache: new CentralConfigCache(dir));

            var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => service.StartAsync(CancellationToken.None));
            Assert.Contains("unreachable", ex.Message);
            Assert.Contains("MCP_ORCHESTRATOR_CONFIG_URL", ex.Message);
        });
    }

    // ----- runtime failures / backoff -----------------------------------------------------------------

    [Fact]
    public async Task Poll_failure_keeps_the_running_config()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, AlphaConfig, ETag: "\"v1\"");

        await InTempDirAsync(async dir =>
        {
            var (trigger, reloader, registry, _) = Wire(server.Url, dir);
            using var _1 = trigger;
            await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None);
            Assert.NotNull(registry.Find("alpha"));

            server.Handler = _ => new(500);

            Assert.Equal(
                HttpConfigPollTrigger.PollOutcome.Failed,
                await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None));
            Assert.NotNull(registry.Find("alpha")); // last-known-good survives
        });
    }

    [Fact]
    public void Backoff_doubles_per_failure_caps_at_15_minutes_and_jitters_10_percent()
    {
        var baseInterval = TimeSpan.FromSeconds(300);
        var random = new Random(42);

        // No jittered sleep anywhere — the delay policy is a pure function.
        AssertDelayRange(baseInterval, failures: 0, random, minSeconds: 270, maxSeconds: 330);
        AssertDelayRange(baseInterval, failures: 1, random, minSeconds: 540, maxSeconds: 660);
        AssertDelayRange(baseInterval, failures: 2, random, minSeconds: 810, maxSeconds: 990); // 1200 capped to 900
        AssertDelayRange(baseInterval, failures: 10, random, minSeconds: 810, maxSeconds: 990); // still capped

        // A configured interval above the cap is never reduced by the backoff cap.
        AssertDelayRange(TimeSpan.FromHours(1), failures: 5, random, minSeconds: 3240, maxSeconds: 3960);

        static void AssertDelayRange(TimeSpan baseInterval, int failures, Random random, double minSeconds, double maxSeconds)
        {
            for (var i = 0; i < 25; i++)
            {
                var delay = HttpConfigPollTrigger.NextDelay(baseInterval, failures, random).TotalSeconds;
                Assert.InRange(delay, minSeconds, maxSeconds);
            }
        }
    }

    // ----- central-only validation ---------------------------------------------------------------------

    [Fact]
    public async Task Config_dir_placeholder_in_a_central_payload_is_rejected_by_name()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, """
            { "capabilities": [ { "name": "alpha", "summary": "A.", "command": "cmd", "args": ["${CONFIG_DIR}/x"] } ] }
            """);

        await InTempDirAsync(async dir =>
        {
            var log = new CollectingLogger();
            var cache = new CentralConfigCache(dir);
            using var trigger = new HttpConfigPollTrigger(Options(server.Url), cache, log);

            Assert.Equal(HttpConfigPollTrigger.PollOutcome.Failed, await trigger.PollOnceAsync(null, CancellationToken.None));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("CONFIG_DIR"));
            Assert.Null(cache.TryRead(server.Url)); // an invalid payload never becomes "last known good"
        });
    }

    [Fact]
    public async Task Oversized_payload_is_rejected()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, $$"""{ "capabilities": [], "pad": "{{new string('x', 1_100_000)}}" }""");

        await InTempDirAsync(async dir =>
        {
            var log = new CollectingLogger();
            using var trigger = new HttpConfigPollTrigger(Options(server.Url), new CentralConfigCache(dir), log);

            Assert.Equal(HttpConfigPollTrigger.PollOutcome.Failed, await trigger.PollOnceAsync(null, CancellationToken.None));
            Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("1 MB"));
        });
    }

    [Fact]
    public async Task Html_content_type_is_rejected_and_last_known_good_retained()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new(200, AlphaConfig, ETag: "\"v1\"");

        await InTempDirAsync(async dir =>
        {
            var (trigger, reloader, registry, _) = Wire(server.Url, dir);
            using var _1 = trigger;
            var log = new CollectingLogger();
            await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None);

            server.Handler = _ => new(200, "<html><body>Sign in</body></html>", ContentType: "text/html");

            Assert.Equal(
                HttpConfigPollTrigger.PollOutcome.Failed,
                await trigger.PollOnceAsync(() => reloader.ReloadAsync(CancellationToken.None), CancellationToken.None));
            Assert.NotNull(registry.Find("alpha"));
        });
    }

    [Fact]
    public void Committed_central_example_passes_central_validation()
    {
        // docs/orchestrator.central.example.json is what the README tells people to point
        // MCP_ORCHESTRATOR_CONFIG_URL at — it must always survive the central-policy load.
        var path = Path.Combine(Demo.SolutionDir, "docs", "orchestrator.central.example.json");
        var loaded = CapabilityCatalog.TryParseForReload(
            File.ReadAllText(path), path,
            builtinPlaceholders: new Dictionary<string, string>(),
            forbidLocalPlaceholders: true, NullLogger.Instance);

        Assert.NotNull(loaded);
        Assert.Equal(new[] { "tokensaver", "files" }, loaded!.Catalog.Names);
    }

    // ----- helpers ------------------------------------------------------------------------------------

    private static CentralConfigOptions Options(string url, string? auth = null, string? ignoredLocalPath = null) =>
        new(new Uri(url), auth, TimeSpan.FromSeconds(300), ignoredLocalPath);

    private static CapabilityRegistry EmptyRegistry() =>
        new(CapabilityCatalog.FromDescriptors(Array.Empty<CapabilityDescriptor>(), NullLogger.Instance));

    private static ILogger<CentralConfigService> Logger(CollectingLogger inner) =>
        new WrappingLogger<CentralConfigService>(inner);

    /// <summary>Full central wiring minus the poll loop: trigger + cache-backed source + reloader.</summary>
    private static (HttpConfigPollTrigger Trigger, ConfigReloader Reloader, CapabilityRegistry Registry, SpyLifecycle Spy)
        Wire(string url, string cacheDir)
    {
        var options = Options(url);
        var cache = new CentralConfigCache(cacheDir);
        var registry = EmptyRegistry();
        var spy = new SpyLifecycle();
        var trigger = new HttpConfigPollTrigger(options, cache, NullLogger.Instance);
        var reloader = new ConfigReloader(
            new CentralConfigSource(options, cache, NullLogger.Instance), registry, spy, NullLogger.Instance);
        return (trigger, reloader, registry, spy);
    }

    private static async Task InTempDirAsync(Func<string, Task> body)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"central-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            await body(dir);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ }
        }
    }

    private static void WithEnv(Action body, params (string Name, string? Value)[] vars)
    {
        var previous = vars.Select(v => (v.Name, Old: Environment.GetEnvironmentVariable(v.Name))).ToList();
        try
        {
            foreach (var (name, value) in vars)
            {
                Environment.SetEnvironmentVariable(name, value);
            }
            body();
        }
        finally
        {
            foreach (var (name, old) in previous)
            {
                Environment.SetEnvironmentVariable(name, old);
            }
        }
    }

    /// <summary>Adapts the collecting logger to the typed ILogger&lt;T&gt; the service constructor wants.</summary>
    private sealed class WrappingLogger<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public WrappingLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }
}
