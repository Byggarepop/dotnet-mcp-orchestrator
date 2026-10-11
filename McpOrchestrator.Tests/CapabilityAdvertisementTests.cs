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

    // ----- Skills-discovery hint and the reserved essential base ----------------------------------

    private static string Summary(int length) => new string('s', length);

    private static CapabilityDescriptor[] NineCapabilities(bool promoteOne = true) =>
        Enumerable.Range(1, 9)
            .Select(i => Cap(
                $"capability-{i}",
                $"Capability number {i}. " + Summary(100),
                instructions: i == 1 ? "CALL THIS WHEN: the task touches capability one. " + Summary(100) : null,
                promote: promoteOne && i == 1))
            .ToArray();

    [Fact]
    public void Skills_hint_is_reserved_before_capability_text_and_the_block_fits()
    {
        var hint = CapabilityAdvertisement.SkillsHint(26);

        var built = CapabilityAdvertisement.BuildServerInstructions(NineCapabilities(), hint);

        Assert.NotNull(built.Text);
        Assert.Contains(hint, built.Text);
        Assert.True(built.Text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars, $"length {built.Text.Length}");
        Assert.False(built.SummariesShortened);
        Assert.False(built.ExceedsBudget);
        // Essential guidance comes first: routing header, then the hint, then the capability list.
        Assert.True(built.Text.IndexOf("'route' to invoke", StringComparison.Ordinal) < built.Text.IndexOf(hint, StringComparison.Ordinal));
        Assert.True(built.Text.IndexOf(hint, StringComparison.Ordinal) < built.Text.IndexOf("Capabilities:", StringComparison.Ordinal));
        for (var i = 1; i <= 9; i++)
        {
            Assert.Contains($"- capability-{i}: Capability number {i}.", built.Text);
        }
    }

    [Fact]
    public void Skills_hint_survives_when_promoted_instructions_overflow_the_budget()
    {
        var hint = CapabilityAdvertisement.SkillsHint(3);
        var capabilities = new[]
        {
            Cap("big", "A.", instructions: new string('a', CapabilityAdvertisement.MaxPromotedInstructionsChars), promote: true),
            Cap("late", "B.", instructions: "LATE-MARKER", promote: true),
        };

        var built = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);

        Assert.Contains(hint, built.Text);
        Assert.Contains("- late: B.", built.Text);
        Assert.Equal(new[] { "big", "late" }, built.OverBudgetPromoted);
        Assert.True(built.Text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
    }

    [Fact]
    public void Base_exactly_at_the_budget_is_kept_whole_and_one_more_char_shortens_summaries()
    {
        var hint = CapabilityAdvertisement.SkillsHint(26);
        var capabilities = NineCapabilities(promoteOne: false);
        var probe = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);
        var slack = CapabilityAdvertisement.MaxTotalInstructionsChars - probe.Text!.Length;
        Assert.InRange(slack, 1, 9 * (CapabilityAdvertisement.MaxSummaryChars - 100 - 30)); // spread below, each summary stays under its cap
        CapabilityDescriptor[] Padded(int extra) => Enumerable.Range(1, 9)
            .Select(i => Cap($"capability-{i}", $"Capability number {i}. " + Summary(100 + extra / 9 + (i == 9 ? extra % 9 : 0))))
            .ToArray();
        capabilities = Padded(slack);

        var exact = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);

        Assert.Equal(CapabilityAdvertisement.MaxTotalInstructionsChars, exact.Text!.Length);
        Assert.False(exact.SummariesShortened);
        Assert.Contains(hint, exact.Text);
        Assert.EndsWith(Summary(100 + slack / 9 + slack % 9), exact.Text);

        capabilities = Padded(slack + 1);
        var over = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);

        Assert.True(over.SummariesShortened);
        Assert.True(over.Text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars, $"length {over.Text.Length}");
        Assert.Contains(hint, over.Text);
        Assert.Contains("- capability-9: ", over.Text);
    }

    [Fact]
    public void Overflowing_base_shortens_summaries_but_keeps_every_capability_and_the_hint()
    {
        var hint = CapabilityAdvertisement.SkillsHint(26);
        var capabilities = Enumerable.Range(1, 12)
            .Select(i => Cap($"cap-{i}", $"Capability {i}. " + Summary(CapabilityAdvertisement.MaxSummaryChars)))
            .ToArray();

        var built = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);

        Assert.True(built.SummariesShortened);
        Assert.False(built.ExceedsBudget);
        Assert.True(built.Text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars, $"length {built.Text.Length}");
        Assert.Contains(hint, built.Text);
        for (var i = 1; i <= 12; i++)
        {
            Assert.Contains($"- cap-{i}: Capability {i}.", built.Text);
        }
    }

    [Fact]
    public void Base_too_large_even_with_minimal_summaries_exceeds_the_budget_visibly_instead_of_dropping_lines()
    {
        var hint = CapabilityAdvertisement.SkillsHint(26);
        var capabilities = Enumerable.Range(1, 40)
            .Select(i => Cap($"capability-number-{i}", $"Capability {i}. " + Summary(CapabilityAdvertisement.MaxSummaryChars)))
            .ToArray();

        var built = CapabilityAdvertisement.BuildServerInstructions(capabilities, hint);

        Assert.True(built.ExceedsBudget);
        Assert.True(built.SummariesShortened);
        Assert.Empty(built.OverBudgetPromoted);
        Assert.Contains(hint, built.Text);
        Assert.Equal(40, capabilities.Count(c => built.Text!.Contains($"- {c.Name}: ", StringComparison.Ordinal)));
    }

    [Fact]
    public void No_capabilities_leaves_the_hint_alone_and_nothing_at_all_yields_null()
    {
        var hint = CapabilityAdvertisement.SkillsHint(2);

        Assert.Equal(hint, CapabilityAdvertisement.BuildServerInstructions(Array.Empty<CapabilityDescriptor>(), hint).Text);
        Assert.Null(CapabilityAdvertisement.BuildServerInstructions(Array.Empty<CapabilityDescriptor>(), null).Text);
    }

    [Fact]
    public async Task Service_includes_the_hint_only_when_skills_are_served_and_catalog_tools_are_on()
    {
        var root = Directory.CreateDirectory(Path.Combine(Path.GetTempPath(), $"adv-skills-{Guid.NewGuid():N}")).FullName;
        try
        {
            File.WriteAllText(
                Path.Combine(Directory.CreateDirectory(Path.Combine(root, "one")).FullName, "SKILL.md"),
                "---\nname: one\ndescription: One.\n---\nbody");
            var skillRegistry = new McpOrchestrator.Orchestration.Skills.SkillRegistry();
            var capabilityRegistry = new CapabilityRegistry(CapabilityCatalog.FromDescriptors(NineCapabilities(), NullLogger.Instance));
            using var skills = new McpOrchestrator.Orchestration.Skills.SkillsReloadService(
                skillRegistry, capabilityRegistry, NullLogger<McpOrchestrator.Orchestration.Skills.SkillsReloadService>.Instance);
            var log = new CollectingLogger();
            var service = new CapabilityAdvertisementService(
                Microsoft.Extensions.Options.Options.Create(new McpServerOptions()), capabilityRegistry,
                new WrappingLogger<CapabilityAdvertisementService>(log), skillRegistry, skills);

            // Empty catalog: no hint, and no mention of the skill tools anywhere.
            var options = new McpServerOptions();
            service.Apply(options, capabilityRegistry.Capabilities);
            Assert.DoesNotContain("list_skills", options.ServerInstructions);

            // One skill, catalog tools on (default): the hint is in the final block, which fits.
            var skillsOptions = new McpOrchestrator.Orchestration.Skills.SkillsOptions
            {
                Sources = { new McpOrchestrator.Orchestration.Skills.SkillSourceOptions { Id = "local", Type = "directory", Path = root } },
            };
            await skills.ApplyAsync(skillsOptions, CancellationToken.None);
            options = new McpServerOptions();
            service.Apply(options, capabilityRegistry.Capabilities);
            Assert.Contains(CapabilityAdvertisement.SkillsHint(1), options.ServerInstructions);
            Assert.True(options.ServerInstructions!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);

            // Catalog tools off: skills are served (as resources) but the tools are not callable,
            // so the hint must not tell the agent to call them.
            skillsOptions.Delivery.CatalogTools = false;
            await skills.ApplyAsync(skillsOptions, CancellationToken.None);
            options = new McpServerOptions();
            service.Apply(options, capabilityRegistry.Capabilities);
            Assert.DoesNotContain("list_skills", options.ServerInstructions);
            Assert.Contains(log.Entries, e => e.Message.Contains("delivery.catalogTools is off"));
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* best-effort cleanup */ }
        }
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

        // And with a realistic skill catalog on top, the hint must fit alongside the full trigger text.
        var withSkills = CapabilityAdvertisement.BuildServerInstructions(
            loaded.Catalog.Capabilities, CapabilityAdvertisement.SkillsHint(26));
        Assert.Contains("list_skills", withSkills.Text);
        Assert.Contains("check_holes", withSkills.Text);
        Assert.Empty(withSkills.OverBudgetPromoted);
        Assert.False(withSkills.SummariesShortened);
        Assert.True(withSkills.Text!.Length <= CapabilityAdvertisement.MaxTotalInstructionsChars);
    }
}
