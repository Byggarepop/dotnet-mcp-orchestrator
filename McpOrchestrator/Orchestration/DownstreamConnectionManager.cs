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
/// retries. Connections are disposed when the host shuts down, or individually when a
/// config reload removes/replaces a capability (<see cref="InvalidateAsync"/>) — in-flight
/// calls against a retiring connection drain before it is disposed.
/// </summary>
public sealed class DownstreamConnectionManager :
    IDownstreamConnectionManager, IDownstreamConnectionLifecycle, IAsyncDisposable
{
    /// <summary>Default connect deadline when a capability does not override it. Generous so a
    /// first-run <c>npx</c> download has time to complete.</summary>
    private const int DefaultConnectTimeoutSeconds = 60;

    /// <summary>Default per-call deadline when a capability does not override it.</summary>
    private const int DefaultCallTimeoutSeconds = 100;

    private readonly ICapabilityCatalog _catalog;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<DownstreamConnectionManager> _logger;
    private readonly Profiling.ISessionTraceWriter _trace;

    // One connection entry per capability. Each entry owns a Lazy connect (a single connect even
    // under concurrent first-use) plus an in-flight counter so a reload can drain it before
    // disposal. Faulted or retired entries are removed so a later call connects fresh.
    private readonly ConcurrentDictionary<string, ConnectionEntry> _clients =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Creates the manager. Connections are opened lazily on first use, not here.</summary>
    /// <param name="trace">
    /// Optional session-trace writer. When supplied (via <c>--trace-out</c>), each successful
    /// discover/route is recorded so the run can be replayed by <c>profile --trace</c>. Defaults to
    /// the no-op writer, so normal operation is unaffected.
    /// </param>
    public DownstreamConnectionManager(
        ICapabilityCatalog catalog,
        ILoggerFactory loggerFactory,
        ILogger<DownstreamConnectionManager> logger,
        Profiling.ISessionTraceWriter? trace = null)
    {
        _catalog = catalog;
        _loggerFactory = loggerFactory;
        _logger = logger;
        _trace = trace ?? Profiling.NullSessionTraceWriter.Instance;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<McpClientTool>> ListToolsAsync(string capability, CancellationToken cancellationToken)
    {
        var descriptor = Resolve(capability);
        var (entry, client) = await AcquireAsync(descriptor, cancellationToken);
        try
        {
            using var call = NewCallScope(descriptor, cancellationToken);
            try
            {
                var tools = await client.ListToolsAsync(new RequestOptions(), call.Token);
                // Manifest is now resident in the agent's context — the load event a trace replay needs.
                _trace.Record("discover_tools", descriptor.Name, tool: null);
                return tools as IReadOnlyList<McpClientTool> ?? tools.ToList();
            }
            catch (OperationCanceledException) when (TimedOut(call, cancellationToken))
            {
                throw Timeout(descriptor, $"Listing tools for capability '{descriptor.Name}'");
            }
        }
        finally
        {
            entry.Exit();
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
        var (entry, client) = await AcquireAsync(descriptor, cancellationToken);
        try
        {
            using var call = NewCallScope(descriptor, cancellationToken);
            try
            {
                var result = await client.CallToolAsync(tool, arguments, cancellationToken: call.Token);
                // The agent routed to this capability — record the interaction for trace replay.
                _trace.Record("route", descriptor.Name, tool);
                return result;
            }
            catch (OperationCanceledException) when (TimedOut(call, cancellationToken))
            {
                throw Timeout(descriptor, $"Tool '{tool}' on capability '{descriptor.Name}'");
            }
        }
        finally
        {
            entry.Exit();
        }
    }

    /// <summary>
    /// Returns what the downstream server declared about itself during the MCP handshake
    /// (connecting to it if needed): its <c>serverInfo.name</c> and optional <c>instructions</c>.
    /// Not on <see cref="IDownstreamConnectionManager"/> — routing never needs it; only setup-time
    /// callers (like <c>init</c>'s summary generation) do, and they hold the concrete manager.
    /// </summary>
    /// <exception cref="CapabilityNotFoundException">No enabled capability has that name.</exception>
    public async Task<ServerHandshake> GetServerHandshakeAsync(string capability, CancellationToken cancellationToken)
    {
        var descriptor = Resolve(capability);
        var (entry, client) = await AcquireAsync(descriptor, cancellationToken);
        try
        {
            return new ServerHandshake(client.ServerInfo?.Name, client.ServerInstructions);
        }
        finally
        {
            entry.Exit();
        }
    }

    /// <inheritdoc />
    public async Task InvalidateAsync(string capability, CancellationToken cancellationToken)
    {
        if (!_clients.TryRemove(capability, out var entry))
        {
            return;
        }

        _logger.LogInformation("Retiring downstream connection for capability '{Name}'.", capability);

        // Wait for in-flight calls to drain. Bounded: each call runs under its own call timeout,
        // so the drain cannot outlive the slowest allowed call.
        await entry.RetireAsync().WaitAsync(cancellationToken);

        if (!entry.ConnectStarted)
        {
            return;
        }

        try
        {
            var client = await entry.ClientTask;
            await client.DisposeAsync();
            _logger.LogInformation("Disposed downstream connection for capability '{Name}'.", capability);
        }
        catch (Exception ex)
        {
            // A faulted connect or a downstream that died mid-dispose — nothing left to release.
            _logger.LogDebug(ex, "Error disposing retired connection '{Name}' (ignored).", capability);
        }
    }

    /// <summary>Resolves a capability name to its descriptor, or throws if it is not in the catalog.</summary>
    private CapabilityDescriptor Resolve(string capability) =>
        _catalog.Find(capability) ?? throw new CapabilityNotFoundException(capability, _catalog.Names);

    /// <summary>
    /// Returns the connection for a capability with its in-flight count incremented — the caller
    /// MUST call <see cref="ConnectionEntry.Exit"/> when its call completes. Creates the entry on
    /// first use; retries when it loses a race against a concurrent retirement.
    /// </summary>
    private async Task<(ConnectionEntry Entry, McpClient Client)> AcquireAsync(
        CapabilityDescriptor descriptor, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            // On a retry we lost a race with a reload retiring this entry — re-check the live
            // catalog so a call against a just-removed capability fails like any unknown name
            // instead of silently respawning the removed server.
            if (attempt > 0 && _catalog.Find(descriptor.Name) is null)
            {
                throw new CapabilityNotFoundException(descriptor.Name, _catalog.Names);
            }

            var entry = _clients.GetOrAdd(descriptor.Name, _ => new ConnectionEntry(descriptor, ConnectAsync));
            if (!entry.TryEnter())
            {
                // Being retired: drop the dead entry if the retirement hasn't already, and retry.
                _clients.TryRemove(new KeyValuePair<string, ConnectionEntry>(descriptor.Name, entry));
                continue;
            }

            try
            {
                // Honour the caller's cancellation while waiting, but let the shared connect
                // run to completion (and stay cached) so one caller giving up doesn't tear it down.
                var client = await entry.ClientTask.WaitAsync(cancellationToken);
                return (entry, client);
            }
            catch (Exception ex)
            {
                entry.Exit();
                if (ex is OperationCanceledException)
                {
                    throw;
                }

                // Connect failed: drop this faulted entry so a later call attempts a fresh connect.
                _clients.TryRemove(new KeyValuePair<string, ConnectionEntry>(descriptor.Name, entry));
                throw;
            }
        }
    }

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
        foreach (var entry in _clients.Values)
        {
            if (!entry.ConnectStarted)
            {
                continue;
            }

            try
            {
                var client = await entry.ClientTask;
                await client.DisposeAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Error disposing a downstream connection (ignored during shutdown).");
            }
        }

        _clients.Clear();
    }

    /// <summary>
    /// One capability's cached connection plus the bookkeeping a hot reload needs: an in-flight
    /// call counter and a retired flag. Retirement stops new entrants and completes a drain task
    /// once the last in-flight call exits, so disposal never yanks a connection mid-call.
    /// </summary>
    private sealed class ConnectionEntry
    {
        private readonly Lazy<Task<McpClient>> _client;
        private readonly object _gate = new();
        private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _inFlight;
        private bool _retired;

        public ConnectionEntry(CapabilityDescriptor descriptor, Func<CapabilityDescriptor, Task<McpClient>> connect)
            => _client = new Lazy<Task<McpClient>>(() => connect(descriptor));

        /// <summary>The shared connect. First awaiter triggers it.</summary>
        public Task<McpClient> ClientTask => _client.Value;

        /// <summary>True when a connect was ever started (so there may be something to dispose).</summary>
        public bool ConnectStarted => _client.IsValueCreated;

        /// <summary>Registers an in-flight call. False when the entry is retired — get a fresh one.</summary>
        public bool TryEnter()
        {
            lock (_gate)
            {
                if (_retired)
                {
                    return false;
                }

                _inFlight++;
                return true;
            }
        }

        /// <summary>Unregisters an in-flight call; releases the drain once retired and empty.</summary>
        public void Exit()
        {
            lock (_gate)
            {
                if (--_inFlight == 0 && _retired)
                {
                    _drained.TrySetResult();
                }
            }
        }

        /// <summary>Stops new entrants and returns a task that completes when in-flight calls have drained.</summary>
        public Task RetireAsync()
        {
            lock (_gate)
            {
                _retired = true;
                if (_inFlight == 0)
                {
                    _drained.TrySetResult();
                }

                return _drained.Task;
            }
        }
    }
}

/// <summary>
/// What a downstream server declared about itself in the MCP <c>initialize</c> response:
/// its self-reported <c>serverInfo.name</c> and the optional <c>instructions</c> hint.
/// </summary>
public sealed record ServerHandshake(string? ServerName, string? Instructions);
