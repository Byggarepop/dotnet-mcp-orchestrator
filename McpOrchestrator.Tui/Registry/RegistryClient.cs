using System.Text.Json;

namespace McpOrchestrator.Tui.Registry;

/// <summary>
/// Minimal client for an MCP registry implementing <c>GET /v0/servers</c> with search and
/// cursor pagination (the official registry, or any subregistry with the same API). One
/// instance per registry source; an injectable handler keeps it testable without a network.
/// </summary>
internal sealed class RegistryClient : IDisposable
{
    private const int PageSize = 50;

    private readonly HttpClient _http;

    /// <summary>
    /// Creates a client for the registry at <paramref name="baseUrl"/>. Tests pass a fake
    /// <paramref name="handler"/> to serve canned responses; production leaves it null.
    /// </summary>
    /// <param name="baseUrl">Registry base url, e.g. "https://registry.modelcontextprotocol.io".</param>
    /// <param name="handler">Optional message handler override for tests.</param>
    public RegistryClient(string baseUrl, HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(30);
    }

    /// <summary>
    /// Fetches one page of servers, optionally filtered by <paramref name="search"/> and
    /// positioned by <paramref name="cursor"/> (from a previous page's <see cref="RegistryPage.NextCursor"/>).
    /// </summary>
    /// <param name="search">Free-text filter, or null for an unfiltered listing.</param>
    /// <param name="cursor">Pagination cursor from the previous page, or null for the first page.</param>
    /// <param name="cancellationToken">Cancels the request (e.g. superseded by newer keystrokes).</param>
    /// <returns>The parsed page; <see cref="RegistryPage.NextCursor"/> is null on the last page.</returns>
    /// <exception cref="HttpRequestException">The registry answered with a non-success status.</exception>
    /// <exception cref="JsonException">The response body was not a valid servers listing.</exception>
    public async Task<RegistryPage> SearchAsync(string? search, string? cursor, CancellationToken cancellationToken)
    {
        var query = new List<string> { $"limit={PageSize}" };
        if (!string.IsNullOrWhiteSpace(search))
            query.Add("search=" + Uri.EscapeDataString(search));
        if (!string.IsNullOrWhiteSpace(cursor))
            query.Add("cursor=" + Uri.EscapeDataString(cursor));
        var uri = "v0/servers?" + string.Join("&", query);

        using var response = await _http.GetAsync(uri, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Registry request failed with status {(int)response.StatusCode} for '{uri}'.");

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var parsed = JsonSerializer.Deserialize(body, RegistryJsonContext.Default.RegistryServersResponse)
            ?? throw new JsonException("Registry response deserialized to null.");

        return new RegistryPage(parsed.Servers ?? new(), parsed.Metadata?.NextCursor);
    }

    public void Dispose() => _http.Dispose();
}
