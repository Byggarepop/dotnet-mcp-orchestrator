using System.Net;
using System.Text;

namespace McpOrchestrator.Tests;

/// <summary>What the test server answers with. ETag, when set, is sent as a response header.</summary>
internal sealed record ResponseSpec(int Status, string Body = "", string ContentType = "application/json", string? ETag = null);

/// <summary>One received request's conditional/auth headers, as the trigger sent them.</summary>
internal sealed record ReceivedRequest(string? IfNoneMatch, string? IfModifiedSince, string? Authorization);

/// <summary>
/// A tiny local <see cref="HttpListener"/> standing in for the central config host — no external
/// network. Tests set <see cref="Handler"/> to script responses (including 304s keyed off the
/// recorded If-None-Match) and read <see cref="Requests"/> to assert what the client sent.
/// </summary>
internal sealed class TestConfigServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly List<ReceivedRequest> _requests = new();

    public TestConfigServer()
    {
        // HttpListener cannot bind port 0; probe random high ports until one is free.
        for (var attempt = 0; ; attempt++)
        {
            var port = Random.Shared.Next(20000, 60000);
            var prefix = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Clear();
            _listener.Prefixes.Add(prefix);
            try
            {
                _listener.Start();
                Url = prefix + "orchestrator.config.json";
                break;
            }
            catch (HttpListenerException) when (attempt < 20)
            {
                // Port taken — try another.
            }
        }

        _ = Task.Run(ServeAsync);
    }

    /// <summary>The config URL to point the trigger at (loopback http — allowed for testing).</summary>
    public string Url { get; }

    /// <summary>Scripts the response per request. Swap it mid-test to simulate server-side changes.</summary>
    public volatile Func<ReceivedRequest, ResponseSpec> Handler = _ => new(200, "{}");

    public IReadOnlyList<ReceivedRequest> Requests
    {
        get { lock (_requests) { return _requests.ToArray(); } }
    }

    private async Task ServeAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception)
            {
                return; // Listener stopped — test is done.
            }

            var received = new ReceivedRequest(
                context.Request.Headers["If-None-Match"],
                context.Request.Headers["If-Modified-Since"],
                context.Request.Headers["Authorization"]);
            lock (_requests)
            {
                _requests.Add(received);
            }

            try
            {
                var spec = Handler(received);
                context.Response.StatusCode = spec.Status;
                if (spec.ETag is not null)
                {
                    context.Response.Headers["ETag"] = spec.ETag;
                }

                if (spec.Status != 304 && spec.Body.Length > 0)
                {
                    var bytes = Encoding.UTF8.GetBytes(spec.Body);
                    context.Response.ContentType = spec.ContentType;
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }

                context.Response.Close();
            }
            catch (Exception)
            {
                // A half-written response just fails that one request; keep serving.
            }
        }
    }

    public void Dispose()
    {
        try
        {
            _listener.Stop();
            _listener.Close();
        }
        catch (ObjectDisposedException)
        {
        }
    }
}
