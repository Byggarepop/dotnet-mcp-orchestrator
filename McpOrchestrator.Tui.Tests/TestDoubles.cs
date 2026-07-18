using System.Net;

namespace McpOrchestrator.Tui.Tests;

/// <summary>
/// Hand-written fake for <see cref="HttpMessageHandler"/> (this repo uses no mocking
/// library): serves queued canned responses and records every request URI so tests can
/// assert on query strings.
/// </summary>
internal sealed class FakeHttpHandler : HttpMessageHandler
{
    private readonly Queue<HttpResponseMessage> _responses = new();

    /// <summary>Every request URI the handler has seen, in order.</summary>
    public List<Uri> Requests { get; } = new();

    /// <summary>Queues the response for the next request.</summary>
    public void Enqueue(HttpStatusCode status, string content) =>
        _responses.Enqueue(new HttpResponseMessage(status) { Content = new StringContent(content) });

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Requests.Add(request.RequestUri!);
        return Task.FromResult(_responses.Dequeue());
    }
}
