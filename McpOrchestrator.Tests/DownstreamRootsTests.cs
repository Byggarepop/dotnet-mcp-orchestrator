using System.IO.Pipelines;
using System.Text.Json;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace McpOrchestrator.Tests;

/// <summary>
/// The MCP <c>roots</c> the orchestrator hands each downstream: derivation from config, file-URI
/// conversion, capability/handler consistency, and what a real SDK server sees after the
/// handshake (an in-memory server over pipes — no process).
/// </summary>
public sealed class DownstreamRootsTests
{
    /// <summary>An absolute directory in the running platform's syntax.</summary>
    private static string AbsDir(params string[] segments) =>
        OperatingSystem.IsWindows()
            ? Path.Combine(@"C:\", Path.Combine(segments))
            : "/" + string.Join("/", segments);

    private static CapabilityDescriptor Cap(string name, string? workingDirectory = null, params string[] roots) => new()
    {
        Name = name,
        Command = "irrelevant",
        WorkingDirectory = workingDirectory,
        Roots = roots.ToList(),
    };

    [Theory]
    [InlineData(@"C:\Addept\Projects\Emulator\WCSEmulator", "file:///C:/Addept/Projects/Emulator/WCSEmulator")]
    [InlineData(@"c:\Addept\Projects\Emulator\WCSEmulator\", "file:///C:/Addept/Projects/Emulator/WCSEmulator")]
    [InlineData(@"C:\My Projects\app #1\100%", "file:///C:/My%20Projects/app%20%231/100%25")]
    [InlineData(@"C:\Users\sebas\Åäö", "file:///C:/Users/sebas/%C3%85%C3%A4%C3%B6")]
    [InlineData(@"C:\", "file:///C:/")]
    [InlineData(@"\\build-srv\share\ws", "file://build-srv/share/ws")]
    [InlineData("/home/dev/ws", "file:///home/dev/ws")]
    public void Converts_absolute_paths_to_file_uris(string path, string expected) =>
        Assert.Equal(expected, DownstreamRoots.ToFileUri(path));

    [Fact]
    public void Working_directory_is_the_default_single_root()
    {
        var ws = AbsDir("Addept", "Projects", "Emulator", "WCSEmulator");

        var root = Assert.Single(DownstreamRoots.For(Cap("chrome", ws)));

        Assert.Equal(DownstreamRoots.ToFileUri(ws), root.Uri);
        Assert.Equal("WCSEmulator", root.Name);
    }

    [Fact]
    public void Explicit_roots_replace_the_working_directory_and_are_deduplicated()
    {
        var wd = AbsDir("wd");
        var a = AbsDir("ws", "a");
        var b = AbsDir("ws", "b");

        var roots = DownstreamRoots.For(Cap("x", wd, a, b + Path.DirectorySeparatorChar, a, " "));

        Assert.Equal([DownstreamRoots.ToFileUri(a), DownstreamRoots.ToFileUri(b)], roots.Select(r => r.Uri));
        Assert.Equal(["a", "b"], roots.Select(r => r.Name));
    }

    [Fact]
    public void Relative_directories_become_absolute_uris()
    {
        var root = Assert.Single(DownstreamRoots.For(Cap("x", "relative-ws")));

        Assert.StartsWith("file:///", root.Uri);
        Assert.EndsWith("/relative-ws", root.Uri);
        Assert.Equal("relative-ws", root.Name);
    }

    [Fact]
    public void Every_downstream_client_pins_the_initialize_handshake_revision()
    {
        // With ProtocolVersion unset the SDK sends a server/discover probe first, which
        // pre-2026-07-28 servers reject with a logged warning before the SDK falls back.
        Assert.Equal("2025-11-25", DownstreamConnectionManager.BuildClientOptions(Cap("bare")).ProtocolVersion);
        Assert.Equal("2025-11-25", DownstreamConnectionManager.BuildClientOptions(Cap("x", AbsDir("ws"))).ProtocolVersion);
    }

    [Fact]
    public void No_directories_means_no_roots_and_no_roots_capability()
    {
        Assert.Empty(DownstreamRoots.For(Cap("bare")));
        var options = DownstreamConnectionManager.BuildClientOptions(Cap("bare"));
        Assert.Null(options.Capabilities?.Roots);
        Assert.Null(options.Handlers?.RootsHandler);
    }

    [Fact]
    public async Task Capability_and_handler_are_advertised_together_with_the_same_roots()
    {
        var ws = AbsDir("ws");

        var options = DownstreamConnectionManager.BuildClientOptions(Cap("x", ws));

        Assert.NotNull(options.Capabilities?.Roots);
        Assert.NotNull(options.Handlers?.RootsHandler);
        var listed = await options.Handlers!.RootsHandler!(null, CancellationToken.None);
        Assert.Equal(DownstreamRoots.ToFileUri(ws), Assert.Single(listed.Roots).Uri);
    }

    [Fact]
    public void Each_capability_gets_only_its_own_workspace()
    {
        var chromeWs = AbsDir("Addept", "Projects", "Emulator", "WCSEmulator");
        var filesWs = AbsDir("src", "other-repo");

        var chrome = Assert.Single(DownstreamRoots.For(Cap("chrome", chromeWs)));
        var files = Assert.Single(DownstreamRoots.For(Cap("files", filesWs)));

        Assert.NotEqual(chrome.Uri, files.Uri);
        Assert.Equal(DownstreamRoots.ToFileUri(chromeWs), chrome.Uri);
        Assert.Equal(DownstreamRoots.ToFileUri(filesWs), files.Uri);
        Assert.DoesNotContain(DownstreamRoots.ToFileUri(Directory.GetCurrentDirectory()), new[] { chrome.Uri, files.Uri });
    }

    [Fact]
    public async Task Server_sees_the_roots_capability_and_lists_exactly_the_workspace()
    {
        var ws = AbsDir("Addept", "Projects", "Emulator", "WCSEmulator");

        var probe = await InMemoryProbe.RunAsync(DownstreamConnectionManager.BuildClientOptions(Cap("chrome", ws)));

        Assert.True(probe.RootsAdvertised);
        var root = Assert.Single(probe.Roots!);
        Assert.Equal(DownstreamRoots.ToFileUri(ws), root.Uri);
        Assert.Equal("WCSEmulator", root.Name);
    }

    [Fact]
    public async Task Server_sees_no_roots_capability_for_a_capability_without_a_workspace()
    {
        // Pins the SDK default the fix relies on: null options advertise no roots at all (the
        // pre-fix behaviour for every capability), rather than roots with an empty list.
        var probe = await InMemoryProbe.RunAsync(DownstreamConnectionManager.BuildClientOptions(Cap("bare")));

        Assert.False(probe.RootsAdvertised);
        Assert.Null(probe.Roots);
    }

    /// <summary>What a downstream server observed about the client's roots during a tool call.</summary>
    private sealed record ProbeResult(bool RootsAdvertised, IList<Root>? Roots);

    /// <summary>
    /// A real SDK server joined to a real SDK client by pipes. Its single tool does what
    /// chrome-devtools-mcp does: inside the call, check whether the client negotiated
    /// <c>roots</c> and, only then, ask <c>roots/list</c>. Inspecting from inside a request is
    /// what works on every protocol revision — on the handshake-less revision two SDK 2.0 peers
    /// negotiate, client capabilities are only visible on the request-scoped server.
    /// </summary>
    private static class InMemoryProbe
    {
        public static async Task<ProbeResult> RunAsync(McpClientOptions? clientOptions)
        {
            var clientToServer = new Pipe();
            var serverToClient = new Pipe();
            var observed = new TaskCompletionSource<ProbeResult>(TaskCreationOptions.RunContinuationsAsynchronously);

            var serverOptions = new McpServerOptions
            {
                ServerInfo = new Implementation { Name = "in-memory", Version = "0.0.0" },
                Handlers = new McpServerHandlers
                {
                    ListToolsHandler = (_, _) => ValueTask.FromResult(new ListToolsResult
                    {
                        Tools = [new Tool { Name = "probe", InputSchema = JsonDocument.Parse("""{"type":"object"}""").RootElement }],
                    }),
                    CallToolHandler = async (request, ct) =>
                    {
                        var advertised = request.Server.ClientCapabilities?.Roots is not null;
                        var roots = advertised ? (await request.Server.RequestRootsAsync(new ListRootsRequestParams(), ct)).Roots : null;
                        observed.TrySetResult(new ProbeResult(advertised, roots));
                        return new CallToolResult { Content = [new TextContentBlock { Text = advertised.ToString() }] };
                    },
                },
            };

            await using var server = McpServer.Create(
                new StreamServerTransport(clientToServer.Reader.AsStream(), serverToClient.Writer.AsStream()),
                serverOptions,
                NullLoggerFactory.Instance);
            using var cts = new CancellationTokenSource();
            var run = server.RunAsync(cts.Token);

            await using (var client = await McpClient.CreateAsync(
                new StreamClientTransport(
                    serverInput: clientToServer.Writer.AsStream(), serverOutput: serverToClient.Reader.AsStream()),
                clientOptions,
                NullLoggerFactory.Instance))
            {
                await client.CallToolAsync("probe", new Dictionary<string, object?>());
            }

            cts.Cancel();
            try { await run.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* shutting down */ }
            return await observed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }
}
