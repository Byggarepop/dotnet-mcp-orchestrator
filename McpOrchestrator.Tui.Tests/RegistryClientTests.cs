using System.Net;
using McpOrchestrator.Tui.Registry;
using Xunit;

namespace McpOrchestrator.Tui.Tests;

/// <summary>
/// Tests for <see cref="RegistryClient"/> against fixture JSON captured from the real
/// registry's /v0/servers responses — parsing, pagination cursors, and query encoding.
/// </summary>
public sealed class RegistryClientTests
{
    private const string BaseUrl = "https://registry.example";

    private static string Fixture(string name) =>
        File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public async Task Parses_real_first_page_fixture_with_remotes_and_cursor()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("registry-servers.json"));
        using var client = new RegistryClient(BaseUrl, handler);

        var page = await client.SearchAsync(null, null, CancellationToken.None);

        Assert.Equal(5, page.Entries.Count);
        Assert.Equal("ac.inference.sh/mcp", page.Entries[0].Server.Name);
        Assert.Equal("streamable-http", page.Entries[0].Server.Remotes![0].Type);
        Assert.Equal("ac.tandem/docs-mcp:0.3.2", page.NextCursor);
    }

    [Fact]
    public async Task Parses_real_search_fixture_with_packages_and_secret_env_vars()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, Fixture("registry-servers-search.json"));
        using var client = new RegistryClient(BaseUrl, handler);

        var page = await client.SearchAsync("slack", null, CancellationToken.None);

        var withPackages = page.Entries.First(e => e.Server.Name == "com.mcparmory/slack");
        Assert.Contains(withPackages.Server.Packages!, p => p.RegistryType == "pypi" && p.Identifier == "mcparmory-slack");

        var withEnv = page.Entries.First(e => e.Server.Name == "io.github.adelaidasofia/slack-mcp");
        var envVar = withEnv.Server.Packages![0].EnvironmentVariables![0];
        Assert.Equal("SLACK_XOXC_TOKEN", envVar.Name);
        Assert.True(envVar.IsRequired);
        Assert.True(envVar.IsSecret);
    }

    [Fact]
    public async Task Passes_cursor_and_escapes_search_in_query()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "servers": [], "metadata": {} }""");
        using var client = new RegistryClient(BaseUrl, handler);

        await client.SearchAsync("hello world", "a/b:1", CancellationToken.None);

        var query = handler.Requests[0].Query;
        Assert.Contains("search=hello%20world", query);
        Assert.Contains("cursor=a%2Fb%3A1", query);
        Assert.StartsWith("/v0/servers", handler.Requests[0].AbsolutePath);
    }

    [Fact]
    public async Task Omits_search_and_cursor_when_not_provided()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.OK, """{ "servers": [], "metadata": {} }""");
        using var client = new RegistryClient(BaseUrl, handler);

        var page = await client.SearchAsync(null, null, CancellationToken.None);

        Assert.DoesNotContain("search=", handler.Requests[0].Query);
        Assert.DoesNotContain("cursor=", handler.Requests[0].Query);
        Assert.Empty(page.Entries);
        Assert.Null(page.NextCursor);
    }

    [Fact]
    public async Task Non_success_status_throws_HttpRequestException()
    {
        var handler = new FakeHttpHandler();
        handler.Enqueue(HttpStatusCode.InternalServerError, "boom");
        using var client = new RegistryClient(BaseUrl, handler);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.SearchAsync(null, null, CancellationToken.None));
    }
}
