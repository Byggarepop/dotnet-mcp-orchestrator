using System.Text.Json;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// The per-project knobs a centrally served catalog exposes: <c>enabled</c> as a <c>${VAR}</c>
/// expression, <c>${VAR:-default}</c> fallbacks, and the central-mode rule that a placeholder the
/// machine cannot supply disables that one capability with an actionable error instead of
/// handing the literal text to the downstream server.
/// </summary>
public sealed class EnabledPlaceholderTests
{
    private static readonly IReadOnlyDictionary<string, string> NoBuiltins = new Dictionary<string, string>();

    private static ReloadedConfig? Parse(string json, bool central, ILogger? logger = null) =>
        CapabilityCatalog.TryParseForReload(json, "test", NoBuiltins, forbidLocalPlaceholders: central, logger ?? NullLogger.Instance);

    private static string Config(string enabled, string args = "[]") => $$"""
        { "capabilities": [ { "name": "db", "summary": "S.", "command": "cmd", "enabled": {{enabled}}, "args": {{args}} } ] }
        """;

    // ----- enabled as an expression ------------------------------------------------------------------

    [Fact]
    public void Enabled_boolean_literals_keep_working()
    {
        Assert.Equal(new[] { "db" }, Parse(Config("true"), central: false)!.Catalog.Names);
        Assert.Empty(Parse(Config("false"), central: false)!.Catalog.Names);
    }

    [Theory]
    [InlineData("\"true\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("\"YES\"", true)]
    [InlineData("\"off\"", false)]
    public void Enabled_boolean_words_as_strings_are_accepted(string enabled, bool expected)
    {
        var loaded = Parse(Config(enabled), central: false)!;
        Assert.Equal(expected, loaded.AllEntries.Single().IsEnabled);
    }

    [Fact]
    public void Enabled_junk_string_is_a_parse_error()
    {
        var log = new CollectingLogger();
        Assert.Null(Parse(Config("\"maybe\""), central: false, log));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("failed to parse"));
    }

    [Theory]
    [InlineData("false", false)]
    [InlineData("0", false)]
    [InlineData("true", true)]
    [InlineData("on", true)]
    public void Enabled_expression_resolves_from_the_environment(string envValue, bool expected)
    {
        using var env = new ScopedEnv("MCP_TEST_USE_DB", envValue);

        var loaded = Parse(Config("\"${MCP_TEST_USE_DB}\""), central: false)!;

        var entry = loaded.AllEntries.Single();
        Assert.Equal(expected, entry.IsEnabled);
        Assert.False(entry.Enabled.IsExpression); // resolved to a literal for the differ
        Assert.Equal(expected ? new[] { "db" } : Array.Empty<string>(), loaded.Catalog.Names);
    }

    [Fact]
    public void Enabled_expression_default_applies_when_variable_is_unset()
    {
        using var env = new ScopedEnv("MCP_TEST_USE_DB", null);

        Assert.Empty(Parse(Config("\"${MCP_TEST_USE_DB:-false}\""), central: false)!.Catalog.Names);
        Assert.Equal(new[] { "db" }, Parse(Config("\"${MCP_TEST_USE_DB:-true}\""), central: false)!.Catalog.Names);
    }

    [Fact]
    public void Enabled_unresolved_expression_is_enabled_with_a_warning_in_local_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_USE_DB", null);
        var log = new CollectingLogger();

        var loaded = Parse(Config("\"${MCP_TEST_USE_DB}\""), central: false, log)!;

        Assert.Equal(new[] { "db" }, loaded.Catalog.Names);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("treating it as enabled"));
    }

    [Fact]
    public void Enabled_expression_resolving_to_a_non_boolean_is_enabled_with_a_warning()
    {
        using var env = new ScopedEnv("MCP_TEST_USE_DB", "sometimes");
        var log = new CollectingLogger();

        var loaded = Parse(Config("\"${MCP_TEST_USE_DB}\""), central: false, log)!;

        Assert.Equal(new[] { "db" }, loaded.Catalog.Names);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("not a boolean"));
    }

    [Fact]
    public void Enabled_unresolved_expression_disables_the_capability_in_central_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_USE_DB", null);
        var log = new CollectingLogger();

        var loaded = Parse(Config("\"${MCP_TEST_USE_DB}\""), central: true, log)!;

        Assert.Empty(loaded.Catalog.Names);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("${MCP_TEST_USE_DB}"));
    }

    // ----- ${VAR:-default} ---------------------------------------------------------------------------

    [Fact]
    public void Default_syntax_falls_back_only_when_the_variable_is_unset()
    {
        using var unset = new ScopedEnv("MCP_TEST_ROOT_A", null);
        using var set = new ScopedEnv("MCP_TEST_ROOT_B", "C:/real");

        var loaded = Parse(Config("true", """["${MCP_TEST_ROOT_A:-C:/fallback}", "${MCP_TEST_ROOT_B:-C:/fallback}", "${MCP_TEST_ROOT_A:-}"]"""), central: false)!;

        Assert.Equal(new[] { "C:/fallback", "C:/real", "" }, loaded.Catalog.Find("db")!.Args);
    }

    [Fact]
    public void Default_syntax_counts_as_resolved_in_central_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_DB_URL", null);
        var log = new CollectingLogger();

        var loaded = Parse(Config("true", """["${MCP_TEST_DB_URL:-postgres://localhost/dev}"]"""), central: true, log)!;

        Assert.Equal(new[] { "db" }, loaded.Catalog.Names);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public void Default_may_itself_be_a_placeholder()
    {
        using var a = new ScopedEnv("MCP_TEST_NEST_A", null);
        using var b = new ScopedEnv("MCP_TEST_NEST_B", "C:/from-b");
        using var c = new ScopedEnv("MCP_TEST_NEST_C", null);

        var loaded = Parse(Config("true", """["${MCP_TEST_NEST_A:-${MCP_TEST_NEST_B}}", "${MCP_TEST_NEST_A:-${MCP_TEST_NEST_C:-last}}", "x${MCP_TEST_NEST_B}y"]"""), central: true)!;

        Assert.Equal(new[] { "C:/from-b", "last", "xC:/from-by" }, loaded.Catalog.Find("db")!.Args);
    }

    [Fact]
    public void Malformed_placeholders_are_left_verbatim()
    {
        var loaded = Parse(Config("true", """["${", "${not valid}", "${A", "$${MCP_TEST_NEST_B:-ok}"]"""), central: false)!;

        Assert.Equal(new[] { "${", "${not valid}", "${A", "$ok" }, loaded.Catalog.Find("db")!.Args);
    }

    [Fact]
    public void Local_placeholder_inside_a_default_is_rejected_in_central_mode()
    {
        var log = new CollectingLogger();

        Assert.Null(Parse(Config("true", """["${MCP_TEST_NEST_A:-${CONFIG_DIR}/x}"]"""), central: true, log));
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("CONFIG_DIR"));
    }

    [Fact]
    public void Committed_central_example_passes_on_a_machine_without_USERPROFILE()
    {
        // The Linux/macOS runners have HOME but no USERPROFILE; the example must stay fully
        // resolvable there, or central mode disables its 'files' capability.
        using var profile = new ScopedEnv("USERPROFILE", null);
        using var home = new ScopedEnv("HOME", "/home/dev");
        var log = new CollectingLogger();
        var path = Path.Combine(Demo.SolutionDir, "docs", "orchestrator.central.example.json");

        var loaded = CapabilityCatalog.TryParseForReload(File.ReadAllText(path), path, NoBuiltins, forbidLocalPlaceholders: true, log)!;

        Assert.Equal(new[] { "tokensaver", "files", "Unwritten" }, loaded.Catalog.Names);
        Assert.Contains("/home/dev", loaded.Catalog.Find("files")!.Args);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    // ----- unresolved placeholders: local stays lenient, central disables with an error ---------------

    [Fact]
    public void Unresolved_placeholder_is_left_literal_in_local_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_DB_URL", null);
        var log = new CollectingLogger();

        var loaded = Parse(Config("true", """["${MCP_TEST_DB_URL}"]"""), central: false, log)!;

        Assert.Equal(new[] { "${MCP_TEST_DB_URL}" }, loaded.Catalog.Find("db")!.Args);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains(log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains("MCP_TEST_DB_URL"));
    }

    [Fact]
    public void Unresolved_placeholder_disables_only_that_capability_in_central_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_DB_URL", null);
        var log = new CollectingLogger();
        const string json = """
            {
              "capabilities": [
                { "name": "db",    "summary": "S.", "command": "cmd", "env": { "DATABASE_URL": "${MCP_TEST_DB_URL}" } },
                { "name": "files", "summary": "S.", "command": "cmd" }
              ]
            }
            """;

        var loaded = Parse(json, central: true, log)!;

        Assert.Equal(new[] { "files" }, loaded.Catalog.Names);
        var db = loaded.AllEntries.Single(e => e.Name == "db");
        Assert.False(db.IsEnabled);
        var error = Assert.Single(log.Entries, e => e.Level == LogLevel.Error);
        Assert.Contains("'db'", error.Message);
        Assert.Contains("${MCP_TEST_DB_URL}", error.Message);
        Assert.Contains(".mcp.json", error.Message);
    }

    [Fact]
    public void Unresolved_placeholder_in_a_disabled_entry_is_not_an_error_in_central_mode()
    {
        using var env = new ScopedEnv("MCP_TEST_DB_URL", null);
        var log = new CollectingLogger();

        var loaded = Parse(Config("false", """["${MCP_TEST_DB_URL}"]"""), central: true, log)!;

        Assert.Empty(loaded.Catalog.Names);
        Assert.DoesNotContain(log.Entries, e => e.Level == LogLevel.Error);
    }

    // ----- round trip ----------------------------------------------------------------------------------

    [Fact]
    public void Write_context_emits_a_literal_as_boolean_and_an_expression_as_string()
    {
        var config = new OrchestratorConfig
        {
            Capabilities =
            {
                new CapabilityDescriptor { Name = "a", Command = "cmd", Enabled = true },
                new CapabilityDescriptor { Name = "b", Command = "cmd", Enabled = EnabledSetting.FromExpression("${USE_B:-false}") },
            },
        };

        var json = JsonSerializer.Serialize(config, Setup.OrchestratorConfigWriteJsonContext.Default.OrchestratorConfig);

        Assert.Contains("\"enabled\": true", json);
        Assert.Contains("\"enabled\": \"${USE_B:-false}\"", json);

        var back = JsonSerializer.Deserialize(json, OrchestratorConfigJsonContext.Default.OrchestratorConfig)!;
        Assert.Equal(EnabledSetting.True, back.Capabilities[0].Enabled);
        Assert.Equal("${USE_B:-false}", back.Capabilities[1].Enabled.Expression);
    }

    /// <summary>Sets (or clears, with <c>null</c>) one environment variable for the test's duration.</summary>
    private sealed class ScopedEnv : IDisposable
    {
        private readonly string _name;
        private readonly string? _previous;

        public ScopedEnv(string name, string? value)
        {
            _name = name;
            _previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose() => Environment.SetEnvironmentVariable(_name, _previous);
    }
}
