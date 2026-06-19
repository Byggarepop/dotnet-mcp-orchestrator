using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace McpOrchestrator.Orchestration;

/// <summary>
/// Default <see cref="IDownstreamConnectionManager"/>. Connects to each downstream MCP
/// server lazily (on first use) over stdio and caches the connection for reuse. Each
/// capability connects at most once; a failed connection is evicted so the next call
/// retries. Connections are disposed when the host shuts down.
/// </summary>
public sealed class DownstreamConnectionManager : IDownstreamConnectionManager, IAsyncDisposable
{
    /// <summary>Default connect deadline when a capability does not override it. Generous so a
    /// first-run <c>npx</c> download has time to complete.</summary>
    private const int DefaultConnectTimeoutSeconds = 60;

    /// <summary>Default per-call deadline when a capability does not override it.</summary>
    private const int DefaultCallTimeoutSeconds = 100;

    private readonly ICapabilityCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownstreamConnectionManager> _logger;

    // One lazily-created connection task per capability. Lazy<T> guarantees a single
    // connect even under concurrent first-use; a faulted entry is evicted (see GetClientAsync).
    private readonly ConcurrentDictionary<string, Lazy<Task<McpClient>>> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the manager. Connections are opened lazily on first use, not here.</summary>
    public DownstreamConnectionManager(
        ICapabilityCatalog catalog,
        ILoggerFactory loggerFactory,
        ILogger<DownstreamConnectionManager> logger)
    {
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = logger;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string capability, CancellationToken cancellationToken)
    {
        var descriptor = Resolve(capability);
        var client = await GetClientAsync(descriptor, cancellationToken);

        using var call = NewCallScope(descriptor, cancellationToken);
        try
        {
            var tools = await client.ListToolsAsync(new RequestOptions(), call.Token);
            return tools as IReadOnlyList<McpClientTool> ?? tools.ToList();
        }
        catch (OperationCanceledException) when (TimedOut(call, cancellationToken))
        {
            throw Timeout(descriptor, $"Listing tools for capability '{descriptor.Name}'");
        }
    }

    /// <inheritdoc />
    public async Task<CallToolResult> CallToolAsync(
        string capability,
        string tool,
        IReadOnlyDictionary<string, object?> arguments,
        CancellationToken cancellationToken)
    {
        var descriptor = Resolve(capability);
        var client = await GetClientAsync(descriptor, cancellationToken);

        using var call = NewCallScope(descriptor, cancellationToken);
        try
        {
            return await client.CallToolAsync(tool, arguments, cancellationToken: call.Token);
        }
        catch (OperationCanceledException) when (TimedOut(call, cancellationToken))
        {
            throw Timeout(descriptor, $"Tool '{tool}' on capability '{descriptor.Name}'");
        }
    }

    /// <summary>Resolves a capability name to its descriptor, or throws if it is not in the catalog.</summary>
    private CapabilityDescriptor Resolve(string capability) =>
        _catalog.Find(capability) ?? throw new CapabilityNotFoundException(capability, _catalog.Names);

    /// <summary>Creates a per-call cancellation scope that fires after the capability's call timeout.</summary>
    private static CancellationTokenSource NewCallScope(CapabilityDescriptor descriptor, CancellationToken caller)
    {
        var cts = CancellationTokenSource.CreateLinkedTokenSource(caller);
        cts.CancelAfter(TimeSpan.FromSeconds(descriptor.CallTimeoutSeconds ?? DefaultCallTimeoutSeconds));
        return cts;
    }

    /// <summary>True when the call's own deadline fired rather than the caller cancelling.</summary>
    private static bool TimedOut(CancellationTokenSource call, CancellationToken caller) =>
        call.IsCancellationRequested && !caller.IsCancellationRequested;

    /// <summary>Builds a descriptive <see cref="TimeoutException"/> for a call that exceeded its deadline.</summary>
    private static TimeoutException Timeout(CapabilityDescriptor descriptor, string what) =>
        new($"{what} timed out after {descriptor.CallTimeoutSeconds ?? DefaultCallTimeoutSeconds}s.");

    /// <summary>
    /// Returns the cached connection for a capability, creating it once on first use. A faulted
    /// connect is evicted so a later call retries; the caller's token only governs the wait, not
    /// the shared connect itself.
    /// </summary>
    private async Task<McpClient> GetClientAsync(CapabilityDescriptor descriptor, CancellationToken cancellationToken)
    {
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

    /// <summary>
    /// Launches a capability's downstream server over stdio and completes the MCP handshake,
    /// subject to the capability's connect timeout. Only the <c>stdio</c> transport is supported.
    /// </summary>
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

        // The connect runs on its own deadline, independent of any one caller's token, so a
        // wedged downstream faults (and gets evicted) instead of leaving every caller awaiting
        // a Lazy<Task> that never completes.
        var timeoutSeconds = d.ConnectTimeoutSeconds ?? DefaultConnectTimeoutSeconds;
        using var connectCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
        McpClient client;
        try
        {
            client = await McpClient.CreateAsync(
                transport,
                clientOptions: null,
                loggerFactory: _loggerFactory,
                cancellationToken: connectCts.Token);
        }
        catch (OperationCanceledException) when (connectCts.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Connecting to capability '{d.Name}' timed out after {timeoutSeconds}s " +
                $"(command: {d.Command} {string.Join(' ', d.Args)}).");
        }

        _logger.LogInformation("Connected to capability '{Name}'.", d.Name);
        return client;
    }

    /// <summary>Disposes every connection that was actually opened. Errors are ignored during shutdown.</summary>
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
