using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// The central-catalog pattern for skills: the shared config lists a <c>directory</c> source
/// whose path is a <c>${VAR}</c> that each project's host config sets. Projects that set it get
/// the skills; projects that do not get the source skipped with a log line naming the variable.
/// </summary>
public sealed class SkillsConfigResolverTests
{
    private static readonly IReadOnlyDictionary<string, string> NoBuiltins = new Dictionary<string, string>();

    private const string Config = """
        {
          "capabilities": [],
          "skills": {
            "sources": [
              { "id": "team",    "type": "git",       "url": "https://example.com/skills.git" },
              { "id": "project", "type": "directory", "path": "${ORCH_TEST_PROJECT_SKILLS}" }
            ]
          }
        }
        """;

    private static SkillsOptions? ParseCentral(ILogger log) =>
        CapabilityCatalog.TryParseForReload(Config, "central", NoBuiltins, forbidLocalPlaceholders: true, log)!
            .Catalog.Skills;

    [Fact]
    public void Central_directory_source_resolves_from_the_projects_environment()
    {
        var dir = Path.Combine(Path.GetTempPath(), "mcporch-skills-" + Guid.NewGuid().ToString("N"));
        Environment.SetEnvironmentVariable("ORCH_TEST_PROJECT_SKILLS", dir);
        try
        {
            var skills = ParseCentral(NullLogger.Instance)!;

            Assert.Equal(["team", "project"], skills.Sources.Select(s => s.Id));
            Assert.Equal(dir, skills.Sources[1].Path);
        }
        finally
        {
            Environment.SetEnvironmentVariable("ORCH_TEST_PROJECT_SKILLS", null);
        }
    }

    [Fact]
    public void Central_directory_source_without_a_value_is_skipped_and_names_the_variable()
    {
        Environment.SetEnvironmentVariable("ORCH_TEST_PROJECT_SKILLS", null);
        var log = new CollectingLogger();

        var skills = ParseCentral(log)!;

        Assert.Equal("team", Assert.Single(skills.Sources).Id);
        Assert.Contains(log.Entries, e =>
            e.Level == LogLevel.Warning
            && e.Message.Contains("source 'project' disabled on this machine")
            && e.Message.Contains("${ORCH_TEST_PROJECT_SKILLS}"));
    }

    [Fact]
    public void Placeholder_default_keeps_the_source_when_the_variable_is_unset()
    {
        Environment.SetEnvironmentVariable("ORCH_TEST_PROJECT_SKILLS", null);
        var options = new SkillsOptions
        {
            Sources = { new SkillSourceOptions { Id = "p", Type = "directory", Path = "${ORCH_TEST_PROJECT_SKILLS:-C:/fallback}" } },
        };

        var resolved = SkillsConfigResolver.Resolve(options, NoBuiltins, forbidLocalPlaceholders: true, NullLogger.Instance)!;

        Assert.Equal("C:/fallback", Assert.Single(resolved.Sources).Path);
    }
}
