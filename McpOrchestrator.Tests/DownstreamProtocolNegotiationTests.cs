using System.IO.Pipelines;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// Downstream servers on an older SDK answer <c>initialize</c> with the revision they support.
/// The pinned client refuses that by itself ("Server protocol version mismatch"); the manager
/// must reconnect at the server's revision when the SDK can speak it, keep failing clearly when
/// it cannot, and never issue the <c>server/discover</c> probe the pin exists to avoid.
/// </summary>
public sealed class DownstreamProtocolNegotiationTests
{
    [Fact]
    public async Task A_server_on_2025_06_18_is_reconnected_at_its_revision()
    {
        var downgrades = new List<(string Requested, string Negotiated)>();
        var factory = new InMemoryServerFactory("2025-06-18");

        await using var client = await DownstreamConnectionManager.CreateClientAsync(
            factory.CreateTransport,
            version => DownstreamConnectionManager.BuildClientOptions(Cap("old"), version),
            NullLoggerFactory.Instance,
            (requested, negotiated) => downgrades.Add((requested, negotiated)),
            CancellationToken.None);

        var tools = await client.ListToolsAsync();
        Assert.Equal("probe", Assert.Single(tools).Name);
        Assert.Equal([("2025-11-25", "2025-06-18")], downgrades);
        Assert.Equal(2, factory.ServersStarted); // the first handshake was spent on the mismatch
        Assert.DoesNotContain(RequestMethods.ServerDiscover, factory.MethodsSeen);
        Assert.Equal(["2025-11-25", "2025-06-18"], factory.RequestedVersions);
    }

    [Fact]
    public async Task A_server_on_the_pinned_revision_connects_once_without_a_downgrade()
    {
        var downgrades = 0;
        var factory = new InMemoryServerFactory("2025-11-25");

        await using var client = await DownstreamConnectionManager.CreateClientAsync(
            factory.CreateTransport,
            version => DownstreamConnectionManager.BuildClientOptions(Cap("current"), version),
            NullLoggerFactory.Instance,
            (_, _) => downgrades++,
            CancellationToken.None);

        Assert.Single(await client.ListToolsAsync());
        Assert.Equal(0, downgrades);
        Assert.Equal(1, factory.ServersStarted);
        Assert.DoesNotContain(RequestMethods.ServerDiscover, factory.MethodsSeen);
    }

    [Fact]
    public async Task The_pinned_client_alone_still_refuses_an_older_server()
    {
        // Documents the SDK behavior the manager works around; if a future SDK negotiates by
        // itself, the downgrade path becomes dead code and this test says so.
        var factory = new InMemoryServerFactory("2025-06-18");

        var ex = await Assert.ThrowsAsync<McpException>(async () =>
        {
            await using var _ = await McpClient.CreateAsync(
                factory.CreateTransport(), DownstreamConnectionManager.BuildClientOptions(Cap("old")), NullLoggerFactory.Instance);
        });

        Assert.Equal("2025-06-18", DownstreamConnectionManager.NegotiateDowngrade(ex.Message, "2025-11-25"));
    }

    [Theory]
    [InlineData("Server protocol version mismatch. Expected 2025-11-25, got 2025-06-18", "2025-06-18")]
    [InlineData("Server protocol version mismatch. Expected 2025-11-25, got 2024-11-05", "2024-11-05")]
    [InlineData("Server protocol version mismatch. Expected 2025-11-25, got 2025-11-25", null)]
    [InlineData("Server protocol version mismatch. Expected 2025-11-25, got 1999-01-01", null)] // unknown revision
    [InlineData("Server protocol version mismatch. Expected 2025-11-25, got 2026-07-28", null)] // no initialize handshake
    [InlineData("Some other McpException", null)]
    public void Only_a_mismatch_naming_an_initialize_capable_revision_is_a_downgrade(string message, string? expected)
        => Assert.Equal(expected, DownstreamConnectionManager.NegotiateDowngrade(message, "2025-11-25"));

    private static CapabilityDescriptor Cap(string name) => new()
    {
        Name = name, Summary = name, Transport = "stdio", Command = "unused", Args = [], Env = new Dictionary<string, string>(),
    };

    /// <summary>
    /// Starts a fresh in-memory SDK server (over pipes, no process) per transport request, pinned
    /// to one protocol revision, and records every request method and initialize revision it saw
    /// by reading the client's JSON-RPC lines on their way in.
    /// </summary>
    private sealed class InMemoryServerFactory(string serverProtocolVersion)
    {
        public int ServersStarted { get; private set; }
        public List<string> MethodsSeen { get; } = [];
        public List<string> RequestedVersions { get; } = [];

        public IClientTransport CreateTransport()
        {
            ServersStarted++;
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                new McpServerOptions
                {
                    ProtocolVersion = serverProtocolVersion,
                    ServerInfo = new Implementation { Name = "in-memory", Version = "0.0.0" },
                    Handlers = new McpServerHandlers
                    {
                        ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                        {
                            Tools = [new Tool { Name = "probe", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement }],
                        }),
                    },
                },
                NullLoggerFactory.Instance);
            _ = server.RunAsync();
            return new StreamClientTransport(
                serverInput: new ObservingStream(clientToServer.Writer.AsStream(), this),
                serverOutput: serverToClient.Reader.AsStream());
        }

        private void Observe(string line)
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.TryGetProperty("method", out var method))
            {
                MethodsSeen.Add(method.GetString()!);
            }

            if (doc.RootElement.TryGetProperty("params", out var p) && p.TryGetProperty("protocolVersion", out var v))
            {
                RequestedVersions.Add(v.GetString()!);
            }
        }

        /// <summary>Passes writes through to the server while parsing each newline-delimited message.</summary>
        private sealed class ObservingStream(Stream inner, InMemoryServerFactory owner) : Stream
        {
            private readonly MemoryStream _pending = new();

            public override bool CanRead => false;
            public override bool CanSeek => false;
            public override bool CanWrite => true;
            public override long Length => throw new NotSupportedException();
            public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
            public override void Flush() => inner.Flush();
            public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();

            public override void Write(byte[] buffer, int offset, int count)
            {
                Scan(buffer.AsSpan(offset, count));
                inner.Write(buffer, offset, count);
            }

            public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            {
                Scan(buffer.Span);
                await inner.WriteAsync(buffer, cancellationToken);
            }

            private void Scan(ReadOnlySpan<byte> bytes)
            {
                foreach (var b in bytes)
                {
                    if (b == (byte)'\n')
                    {
                        var line = System.Text.Encoding.UTF8.GetString(_pending.ToArray()).Trim();
                        _pending.SetLength(0);
                        if (line.Length > 0) owner.Observe(line);
                    }
                    else
                    {
                        _pending.WriteByte(b);
                    }
                }
            }
        }
    }
}
