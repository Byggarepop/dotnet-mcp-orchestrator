using McpOrchestrator.Orchestration.Skills;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

public sealed class HttpSkillSourceTests
{
    private const string SkillMd = "---\nname: web-skill\ndescription: A remotely served skill.\n---\nRemote body.";

    private static string IndexJson(bool withFiles) =>
        $$"""
        {
          "$schema": "https://schemas.agentskills.io/discovery/0.2.0/schema.json",
          "skills": [
            {
              "name": "web-skill",
              "type": "skill-md",
              "description": "A remotely served skill.",
              "url": "web-skill/SKILL.md"{{(withFiles ? ", \"files\": [\"references/notes.md\"]" : "")}}
            }
          ]
        }
        """;

    [Fact]
    public async Task Loads_skills_from_an_index_with_supporting_files_and_sends_the_auth_header()
    {
        using var server = new TestConfigServer();
        server.Handler = request => request.Path switch
        {
            "/index.json" => new ResponseSpec(200, IndexJson(withFiles: true)),
            "/web-skill/SKILL.md" => new ResponseSpec(200, SkillMd, "text/markdown"),
            "/web-skill/references/notes.md" => new ResponseSpec(200, "remote notes", "text/markdown"),
            _ => new ResponseSpec(404),
        };
        var baseUrl = server.Url.Replace("orchestrator.config.json", "index.json");
        using var source = new HttpSkillSource("cdn", new Uri(baseUrl), "Bearer secret-token", NullLogger.Instance);

        var skills = await source.LoadAsync(CancellationToken.None);

        var skill = Assert.Single(skills);
        Assert.Equal("web-skill", skill.Name);
        Assert.Equal("Remote body.", skill.Body);
        Assert.Contains(skill.Files, f => f.RelativePath == "references/notes.md");
        Assert.All(server.Requests, r => Assert.Equal("Bearer secret-token", r.Authorization));
    }

    [Fact]
    public async Task Invalid_file_paths_in_the_index_are_skipped_not_fetched()
    {
        using var server = new TestConfigServer();
        var index = IndexJson(withFiles: true).Replace("references/notes.md", "../../outside.txt");
        server.Handler = request => request.Path switch
        {
            "/index.json" => new ResponseSpec(200, index),
            "/web-skill/SKILL.md" => new ResponseSpec(200, SkillMd, "text/markdown"),
            _ => new ResponseSpec(404),
        };
        var baseUrl = server.Url.Replace("orchestrator.config.json", "index.json");
        using var source = new HttpSkillSource("cdn", new Uri(baseUrl), null, NullLogger.Instance);

        var skills = await source.LoadAsync(CancellationToken.None);

        var skill = Assert.Single(skills);
        Assert.Single(skill.Files); // SKILL.md only; the traversal path was never requested.
        Assert.DoesNotContain(server.Requests, r => r.Path?.Contains("outside") == true);
    }

    [Fact]
    public async Task Unreachable_index_yields_no_skills_without_throwing()
    {
        using var server = new TestConfigServer();
        server.Handler = _ => new ResponseSpec(500);
        var baseUrl = server.Url.Replace("orchestrator.config.json", "index.json");
        using var source = new HttpSkillSource("cdn", new Uri(baseUrl), null, NullLogger.Instance);

        Assert.Empty(await source.LoadAsync(CancellationToken.None));
    }
}
