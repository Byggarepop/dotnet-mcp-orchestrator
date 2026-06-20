using System.Net;
using McpOrchestrator.Orchestration.LocalLlm;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Tests the model download/validation in <see cref="ModelProvisioner"/> with a stub HTTP handler —
/// no network, no real model. Covers the GGUF magic-byte / size validation and retry behavior.
/// </summary>
public sealed class ModelProvisionerTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mcp-orch-model").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    private LocalLlmOptions Options() => new()
    {
        Enabled = true,
        CacheDirectory = _dir,
        ModelFileName = "test-model.gguf",
        ModelUrl = "https://example.invalid/model.gguf",
    };

    /// <summary>A handler that returns a queued sequence of byte-body responses, one per request.</summary>
    private sealed class QueueHandler : HttpMessageHandler
    {
        private readonly Queue<byte[]> _bodies;
        public int Calls { get; private set; }

        public QueueHandler(params byte[][] bodies) => _bodies = new Queue<byte[]>(bodies);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Calls++;
            var body = _bodies.Dequeue();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(body),
            });
        }
    }

    private static byte[] Gguf(params byte[] tail) => "GGUF"u8.ToArray().Concat(tail).ToArray();

    [Fact]
    public async Task Downloads_and_validates_a_gguf_file()
    {
        var handler = new QueueHandler(Gguf(1, 2, 3, 4));
        var provisioner = new ModelProvisioner(Options(), NullLogger.Instance, new HttpClient(handler));

        var path = await provisioner.EnsureModelAsync(CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.Equal(1, handler.Calls);
        Assert.Equal("GGUF"u8.ToArray(), File.ReadAllBytes(path)[..4]);
        Assert.False(File.Exists(path + ".partial")); // cleaned up by the atomic move
    }

    [Fact]
    public async Task Rejects_non_gguf_body_and_gives_up_after_retries()
    {
        // An HTML error page masquerading as the model — must never be saved as the model.
        var html = "<html>Not found</html>"u8.ToArray();
        var handler = new QueueHandler(html, html, html);
        var provisioner = new ModelProvisioner(Options(), NullLogger.Instance, new HttpClient(handler));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.EnsureModelAsync(CancellationToken.None));

        Assert.Equal(3, handler.Calls);                 // retried up to the limit
        Assert.Empty(Directory.GetFiles(_dir));         // nothing left behind
    }

    [Fact]
    public async Task Retries_then_succeeds_on_a_later_attempt()
    {
        var handler = new QueueHandler("oops"u8.ToArray(), Gguf(9, 9));
        var provisioner = new ModelProvisioner(Options(), NullLogger.Instance, new HttpClient(handler));

        var path = await provisioner.EnsureModelAsync(CancellationToken.None);

        Assert.Equal(2, handler.Calls);
        Assert.True(File.Exists(path));
    }

    [Fact]
    public async Task Returns_cached_file_without_downloading()
    {
        var options = Options();
        File.WriteAllBytes(options.ResolvedModelPath, Gguf(0));
        var handler = new QueueHandler(); // would throw if called (empty queue)
        var provisioner = new ModelProvisioner(options, NullLogger.Instance, new HttpClient(handler));

        var path = await provisioner.EnsureModelAsync(CancellationToken.None);

        Assert.Equal(options.ResolvedModelPath, path);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Missing_explicit_model_path_throws()
    {
        var options = Options();
        options.ModelPath = Path.Combine(_dir, "does-not-exist.gguf");
        var provisioner = new ModelProvisioner(options, NullLogger.Instance, new HttpClient(new QueueHandler()));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => provisioner.EnsureModelAsync(CancellationToken.None));
    }
}
