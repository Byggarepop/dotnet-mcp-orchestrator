using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace ConsafeWorkflow.Mcp.Orchestration;

/// <summary>
/// Default <see cref="IDownstreamConnectionManager"/>. Connects to each downstream MCP
/// server lazily (on first use) over stdio and caches the connection for reuse. Each
/// capability connects at most once; a failed connection is evicted so the next call
/// retries. Connections are disposed when the host shuts down.
/// </summary>
public sealed class DownstreamConnectionManager : IDownstreamConnectionManager, IAsyncDisposable
{
    private readonly ICapabilityCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownstreamConnectionManager> _logger;

    // One lazily-created connection task per capability. Lazy<T> guarantees a single
    // connect even under concurrent first-use; a faulted entry is evicted (see GetClientAsync).
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    public DownstreamConnectionManager(
        ICapabilityCatalog catalog,
        ILoggerFactory loggerFactory,
        ILogger<DownstreamConnectionManager> logger)
    {
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string capability, CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(capability, cancellationToken);
        var tools = await client.ListToolsAsync(new RequestOptions(), cancellationToken);
        return tools as IReadOnlyList<McpClientTool> ?? tools.ToList();
    }

    public async Task<CallToolResult> CallToolAsync(
        string capability,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var client = await GetClientAsync(capability, cancellationToken);
        return await client.CallToolAsync(tool, arguments, cancellationToken: cancellationToken);
    }

    private async Task<McpClient> GetClientAsync(string capability, CancellationToken cancellationToken)
    {
        var descriptor = _catalog.Find(capability)
            ?? throw new CapabilityNotFoundException(capability, _catalog.Names);

        var lazy = _clients.GetOrAdd(
            descriptor.Name,
            _ => new Lazy<Task<McpClient>>(() => ConnectAsync(descriptor)));

        try
        {
            // Honour the caller's cancellation while waiting, but let the shared connect
            // run to completion (and stay cached) so one caller giving up doesn't tear it down.
            return await lazy.Value.WaitAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Connect failed: drop this faulted entry so a later call attempts a fresh connect.
            _clients.TryRemove(new KeyValuePair<string, Lazy<Task<McpClient>>>(descriptor.Name, lazy));
            throw;
        }
    }

    private async Task<McpClient> ConnectAsync(CapabilityDescriptor d)
    {
        if (!string.Equals(d.Transport, "stdio", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException(
                $"Capability '{d.Name}' uses transport '{d.Transport}', but only 'stdio' is supported.");
        }

        _logger.LogInformation(
            "Connecting to capability '{Name}': {Command} {Args}",
            d.Name, d.Command, string.Join(' ', d.Args));

        var transport = new StdioClientTransport(
            new StdioClientTransportOptions
            {
                Name = d.Name,
                Command = d.Command,
                Arguments = d.Args,
                WorkingDirectory = d.WorkingDirectory,
                EnvironmentVariables = d.Env.Count == 0 ? null : d.Env,
            },
            _loggerFactory);

        var client = await McpClient.CreateAsync(
            transport,
            clientOptions: null,
            loggerFactory: _loggerFactory,
            cancellationToken: CancellationToken.None);

        _logger.LogInformation("Connected to capability '{Name}'.", d.Name);
        return client;
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var lazy in _clients.Values)
        {
            if (!lazy.IsValueCreated)
            {
                continue;
            }

            try
            {
                var client = await lazy.Value;
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing a downstream connection (ignored during shutdown).");
            }
        }

        _clients.Clear();
    }
}
