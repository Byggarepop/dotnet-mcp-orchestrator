using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Server;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests for the session-start catalog advertisement: the server-level instructions generated
/// for the initialize handshake (<see cref="CapabilityAdvertisement.BuildServerInstructions"/>),
/// the catalog scent appended to the <c>list_capabilities</c> tool description, and the hosted
/// service that applies both to the live <see cref="McpServerOptions"/>.
/// </summary>
public sealed class CapabilityAdvertisementTests
{
    private static CapabilityDescriptor Cap(
        string name, string summary = "", string? instructions = null, bool promote = false) => new()
    {
        Name = name,
        Summary = summary,
        Instructions = instructions,
        Promote = promote,
        Command = "cmd",
    };

    // ----- BuildServerInstructions ----------------------------------------------------------------

    [Fact]
    public void BuildServerInstructions_returns_null_for_empty_catalog()
    {
        Assert.Null(CapabilityAdvertisement.BuildServerInstructions(Array.Empty<CapabilityDescriptor>()));
    }

    [Fact]
    public void BuildServerInstructions_lists_every_capability_with_summary()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("jira", "Issue tracking."),
            Cap("codegen", "Scaffold classes."),
        });

        Assert.NotNull(text);
        Assert.Contains("- jira: Issue tracking.", text);
        Assert.Contains("- codegen: Scaffold classes.", text);
        Assert.Contains("list_capabilities", text); // points the agent at the full catalog
    }

    [Fact]
    public void BuildServerInstructions_omits_instructions_unless_promoted()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("jira", "Issue tracking.", instructions: "ALWAYS pass the issue key."),
        });

        Assert.DoesNotContain("ALWAYS pass the issue key.", text);
    }

    [Fact]
    public void BuildServerInstructions_hoists_promoted_instructions()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("guard", "Convention guard.",
                instructions: "CALL THIS WHEN:\n- You changed any file.", promote: true),
            Cap("jira", "Issue tracking.", instructions: "Not promoted."),
        });

        Assert.Contains("CALL THIS WHEN:", text);
        Assert.Contains("- You changed any file.", text);
        Assert.DoesNotContain("Not promoted.", text);
    }

    [Fact]
    public void BuildServerInstructions_truncates_promoted_instructions_with_a_note()
    {
        var longText = new string('x', CapabilityAdvertisement.MaxPromotedInstructionsChars + 500);
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("guard", "Guard.", instructions: longText, promote: true),
        });

        Assert.NotNull(text);
        Assert.Contains("truncated — call 'list_capabilities'", text);
        // The hoisted block stays bounded: cap + note + surrounding scaffolding, never the full text.
        Assert.True(text!.Length < CapabilityAdvertisement.MaxPromotedInstructionsChars + 500);
    }

    [Fact]
    public void BuildServerInstructions_collapses_and_caps_the_summary_line()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("multi", "line one\nline two"),
            Cap("long", new string('s', CapabilityAdvertisement.MaxSummaryChars + 100)),
        });

        Assert.Contains("- multi: line one line two", text);
        Assert.DoesNotContain(new string('s', CapabilityAdvertisement.MaxSummaryChars + 100), text);
    }

    // ----- Total block budget ---------------------------------------------------------------------

    [Fact]
    public void BuildServerInstructions_under_budget_reports_nothing_over_budget()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("guard", "Guard.", instructions: "CALL THIS WHEN: always.", promote: true),
            Cap("jira", "Issue tracking."),
        }, out var overBudget);

        Assert.Empty(overBudget);
        Assert.Contains("CALL THIS WHEN: always.", text);
        Assert.True(text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
    }

    [Fact]
    public void BuildServerInstructions_truncates_the_entry_crossing_the_total_budget()
    {
        // First promoted entry fits; the second crosses the total budget and gets the note.
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("first", "A.", instructions: "FIRST-MARKER " + new string('a', 800), promote: true),
            Cap("second", "B.", instructions: "SECOND-MARKER " + new string('b', 1_500), promote: true),
        }, out var overBudget);

        Assert.Equal(new[] { "second" }, overBudget);
        Assert.NotNull(text);
        Assert.True(text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
        Assert.Contains("FIRST-MARKER", text);
        Assert.Contains(new string('a', 800), text); // first entry intact
        Assert.Contains("SECOND-MARKER", text);      // second starts…
        Assert.DoesNotContain(new string('b', 1_500), text); // …but is cut
        Assert.Contains("truncated — call 'list_capabilities'", text);
    }

    [Fact]
    public void BuildServerInstructions_omits_promoted_entries_after_the_first_over_budget_one()
    {
        var text = CapabilityAdvertisement.BuildServerInstructions(new[]
        {
            Cap("big", "A.", instructions: new string('a', CapabilityAdvertisement.MaxPromotedInstructionsChars), promote: true),
            Cap("late", "B.", instructions: "LATE-MARKER never advertised.", promote: true),
        }, out var overBudget);

        Assert.Equal(new[] { "big", "late" }, overBudget);
        Assert.True(text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
        Assert.DoesNotContain("LATE-MARKER", text);
        Assert.Contains("- late: B.", text); // the summary line always survives
        Assert.Contains("truncated — call 'list_capabilities'", text);
    }

    [Fact]
    public void Service_warns_when_the_budget_drops_promoted_instructions()
    {
        var log = new CollectingLogger();
        var options = new McpServerOptions();
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors(
            new[]
            {
                Cap("big", "A.", instructions: new string('a', CapabilityAdvertisement.MaxPromotedInstructionsChars), promote: true),
                Cap("late", "B.", instructions: "Never fits.", promote: true),
            },
            NullLogger.Instance));
        var service = new CapabilityAdvertisementService(
            Microsoft.Extensions.Options.Options.Create(options), registry,
            new WrappingLogger<CapabilityAdvertisementService>(log));

        service.Apply(options, registry.Capabilities);

        var warning = Assert.Single(log.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains("big, late", warning.Message);
        Assert.Contains(CapabilityAdvertisement.MaxTotalInstructionsChars.ToString(), warning.Message);
    }

    private sealed class WrappingLogger<T> : ILogger<T>
    {
        private readonly ILogger _inner;
        public WrappingLogger(ILogger inner) => _inner = inner;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => _inner.BeginScope(state);
        public bool IsEnabled(LogLevel logLevel) => _inner.IsEnabled(logLevel);
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            _inner.Log(logLevel, eventId, state, exception, formatter);
    }

    // ----- AppendCatalogScent ---------------------------------------------------------------------

    [Fact]
    public void AppendCatalogScent_leaves_description_unchanged_for_empty_catalog()
    {
        Assert.Equal("Base.", CapabilityAdvertisement.AppendCatalogScent("Base.", Array.Empty<CapabilityDescriptor>()));
    }

    [Fact]
    public void AppendCatalogScent_appends_names_with_short_summaries()
    {
        var text = CapabilityAdvertisement.AppendCatalogScent("Base.", new[]
        {
            Cap("jira", "Issue tracking."),
            Cap("bare"),
        });

        Assert.StartsWith("Base. Currently registered:", text);
        Assert.Contains("jira (Issue tracking.)", text);
        Assert.Contains("bare", text);
    }

    [Fact]
    public void AppendCatalogScent_truncates_long_summaries()
    {
        var text = CapabilityAdvertisement.AppendCatalogScent("Base.", new[]
        {
            Cap("verbose", new string('v', CapabilityAdvertisement.MaxScentSummaryChars + 200)),
        });

        Assert.DoesNotContain(new string('v', CapabilityAdvertisement.MaxScentSummaryChars + 200), text);
        Assert.Contains("…", text);
    }

    // ----- CapabilityAdvertisementService ---------------------------------------------------------

    [Fact]
    public void Service_sets_server_instructions_and_patches_the_tool_description()
    {
        var options = new McpServerOptions
        {
            ToolCollection = new McpServerPrimitiveCollection<McpServerTool>
            {
                McpServerTool.Create(() => "ok", new McpServerToolCreateOptions
                {
                    Name = "list_capabilities",
                    Description = "Base description.",
                }),
            },
        };
        var registry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors(
            new[] { Cap("guard", "Convention guard.", instructions: "CALL THIS WHEN: always.", promote: true) },
            NullLogger.Instance));
        var service = new CapabilityAdvertisementService(
            Microsoft.Extensions.Options.Options.Create(options), registry,
            NullLogger<CapabilityAdvertisementService>.Instance);

        service.Apply(options, registry.Capabilities);

        Assert.Contains("guard: Convention guard.", options.ServerInstructions);
        Assert.Contains("CALL THIS WHEN: always.", options.ServerInstructions);
        Assert.True(options.ToolCollection.TryGetPrimitive("list_capabilities", out var tool));
        Assert.StartsWith("Base description. Currently registered:", tool!.ProtocolTool.Description);
        Assert.Contains("guard (Convention guard.)", tool.ProtocolTool.Description);
    }

    // ----- Acceptance: the committed central example ----------------------------------------------

    [Fact]
    public void Central_example_handshake_instructions_carry_Unwritten_trigger_text()
    {
        // The committed example catalog promotes Unwritten; a fresh initialize handshake against
        // it must contain the capability name and its CALL-THIS-WHEN trigger lines so an agent
        // sees them from turn one without ever calling list_capabilities.
        var path = Path.Combine(Demo.SolutionDir, "docs", "orchestrator.central.example.json");
        var loaded = CapabilityCatalog.TryParseForReload(
            File.ReadAllText(path), path,
            builtinPlaceholders: new Dictionary<string, string>(),
            forbidLocalPlaceholders: true, NullLogger.Instance);
        Assert.NotNull(loaded);

        var text = CapabilityAdvertisement.BuildServerInstructions(loaded!.Catalog.Capabilities, out var overBudget);

        Assert.NotNull(text);
        Assert.Contains("Unwritten", text);
        Assert.Contains("CALL THIS WHEN", text);
        Assert.Contains("check_holes", text);
        // The example must fit the total budget whole — it is the catalog people copy, and the
        // budget exists because Claude Code truncates the rendered block at ~2,048 chars.
        Assert.Empty(overBudget);
        Assert.True(text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
    }
}
