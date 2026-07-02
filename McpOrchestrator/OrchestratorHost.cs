using System.Diagnostics;
using System.Reflection;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator;

/// <summary>
/// Builds and runs the orchestrator MCP server: it exposes the meta-tools to the agent and proxies
/// calls to the downstream MCP servers in the catalog. A pure relay — it never interprets the
/// agent's input, it forwards exactly what the agent sends.
/// </summary>
public static class OrchestratorHost
{
    /// <summary>
    /// Runs the server over stdio until the host shuts down, or dispatches the <c>profile</c>
    /// subcommand. Returns a process exit code.
    /// </summary>
    public static async Task<int> RunAsync(string[] args)
    {
        // `mcp-orchestrator profile …` is a one-shot CLI, not the MCP server. Dispatch it before
        // anything server-related (host build, debug gate) so it never blocks or opens stdio.
        if (args.Length > 0 && string.Equals(args[0], "profile", StringComparison.OrdinalIgnoreCase))
        {
            return await Profiling.ProfileCommand.RunAsync(args[1..]);
        }

        // `mcp-orchestrator init <host-config>` adopts an existing MCP host config into an
        // orchestrator setup (one-shot CLI). Like `profile`, it must never open stdio.
        if (args.Length > 0 && string.Equals(args[0], "init", StringComparison.OrdinalIgnoreCase))
        {
            return await Setup.InitCommand.RunAsync(args[1..]);
        }

        RunDebugGateIfRequested();

        var builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for the MCP stdio protocol — all logging must go to stderr.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Mirror the log to a file under the user's profile (stderr from an MCP child is easy to
        // lose). Default: %USERPROFILE%/.mcpOrchestrator/orchestrator.log; override the
        // directory with MCP_ORCHESTRATOR_LOG_DIR, or disable with MCP_ORCHESTRATOR_LOG_DIR=off.
        var fileLogger = Diagnostics.FileLoggerProvider.Create();
        if (fileLogger is not null)
        {
            builder.Logging.AddProvider(fileLogger);
            Console.Error.WriteLine($"[McpOrchestrator] Logging to {fileLogger.FilePath}");
        }

        // Orchestration services:
        //  - CapabilityRegistry / ICapabilityCatalog: the address book of downstream MCPs. The
        //    registry wraps the loaded catalog in a hot-swappable holder so a config reload can
        //    replace the snapshot at runtime; every reader resolves through it.
        //  - IDownstreamConnectionManager: connects to / proxies calls to those MCPs (MCP client).
        //    Also registered as IDownstreamConnectionLifecycle so the reload pipeline can retire
        //    connections of removed/changed capabilities.
        // Source selection is binary: MCP_ORCHESTRATOR_CONFIG_URL set → centrally managed config
        // (the local file path is ignored entirely); unset → local file mode, exactly as before.
        // Central and local configs are never merged.
        Orchestration.Reload.CentralConfigOptions? centralConfig;
        try
        {
            centralConfig = Orchestration.Reload.CentralConfigOptions.FromEnvironment();
        }
        catch (ArgumentException ex)
        {
            Console.Error.WriteLine($"[McpOrchestrator] {ex.Message}");
            return 1;
        }

        var contentRoot = builder.Environment.ContentRootPath;
        builder.Services.AddSingleton<CapabilityRegistry>(sp => centralConfig is null
            ? new CapabilityRegistry(CapabilityCatalog.Load(
                contentRoot,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CapabilityCatalog>()))
            // Central mode: seeded empty; CentralConfigService fills it (fetch or cache) before
            // the MCP server hosted service starts accepting the stdio session.
            : new CapabilityRegistry(CapabilityCatalog.FromDescriptors(
                Array.Empty<CapabilityDescriptor>(), Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance)));
        builder.Services.AddSingleton<ICapabilityCatalog>(sp => sp.GetRequiredService<CapabilityRegistry>());
        builder.Services.AddSingleton<DownstreamConnectionManager>();
        builder.Services.AddSingleton<IDownstreamConnectionManager>(sp => sp.GetRequiredService<DownstreamConnectionManager>());
        builder.Services.AddSingleton<IDownstreamConnectionLifecycle>(sp => sp.GetRequiredService<DownstreamConnectionManager>());

        // Config reload trigger, one of the two (registered before AddMcpServer so its StartAsync
        // — including central mode's initial fetch — completes before the MCP session opens):
        //  - file mode: watch the config file (on by default; MCP_ORCHESTRATOR_NO_RELOAD=1 opts out).
        //  - central mode: initial fetch (or cache fallback) + polling with ETag revalidation.
        if (centralConfig is null)
        {
            builder.Services.AddHostedService<Orchestration.Reload.ConfigHotReloadService>();
        }
        else
        {
            builder.Services.AddSingleton(centralConfig);
            builder.Services.AddHostedService<Orchestration.Reload.CentralConfigService>();
        }

        // Optional session-trace side-channel (--trace-out <path> or MCP_ORCHESTRATOR_TRACE_OUT).
        // The connection manager picks this up by DI and records each discover/route so the run can
        // later be replayed with `profile --trace`. Off → a no-op writer, zero overhead.
        var traceOutPath = ResolveTraceOutPath(args);
        if (traceOutPath is not null)
        {
            var traceWriter = new Profiling.JsonlSessionTraceWriter(traceOutPath);
            builder.Services.AddSingleton<Profiling.ISessionTraceWriter>(traceWriter);
            Console.Error.WriteLine($"[McpOrchestrator] Session trace → {traceWriter.FilePath}");
        }
        else
        {
            builder.Services.AddSingleton<Profiling.ISessionTraceWriter>(Profiling.NullSessionTraceWriter.Instance);
        }

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            // Register the meta-tools via the generic overload (source-generated, Native-AOT safe)
            // rather than the reflection-based WithToolsFromAssembly.
            .WithTools<Tools.OrchestratorTool>();

        var app = builder.Build();

        // Opt-in self-update of the native binary (see SelfUpdater). Runs in the background so it
        // never delays startup, and applies to the *next* launch — the current session is untouched.
        if (Update.SelfUpdater.IsEnabled)
        {
            Update.SelfUpdater.CleanupOldBinary();
            var updateLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("SelfUpdater");
            _ = Task.Run(() => Update.SelfUpdater.CheckAndStageAsync(updateLogger, CancellationToken.None));
        }

        try
        {
            await app.RunAsync();
        }
        catch (Exception ex)
        {
            // A hosted service refused to start (e.g. central config unreachable with no cache).
            // The reason was already logged; restate it on stderr and exit non-zero.
            Console.Error.WriteLine($"[McpOrchestrator] Fatal: {ex.Message}");
            return 1;
        }

        return 0;
    }

    /// <summary>
    /// Resolves the session-trace output path from <c>--trace-out &lt;path&gt;</c> (or
    /// <c>--trace-out=&lt;path&gt;</c>) on the command line, falling back to the
    /// <c>MCP_ORCHESTRATOR_TRACE_OUT</c> environment variable. Returns <c>null</c> when tracing is off.
    /// </summary>
    internal static string? ResolveTraceOutPath(string[] args)
    {
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] == "--trace-out")
            {
                return i + 1 < args.Length ? args[i + 1] : null;
            }
            if (args[i].StartsWith("--trace-out=", StringComparison.Ordinal))
            {
                return args[i]["--trace-out=".Length..];
            }
        }

        var fromEnv = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_TRACE_OUT");
        return string.IsNullOrWhiteSpace(fromEnv) ? null : fromEnv;
    }

    /// <summary>
    /// Optional debug gate. The IDE launches this server as a child process, so the way to debug
    /// it is to attach to the spawned process. This pauses startup until a debugger attaches.
    /// Never writes to stdout (reserved for the MCP protocol).
    ///   MCP_ORCHESTRATOR_DEBUG=launch -> Visual Studio Just-In-Time debugger picker.
    ///   MCP_ORCHESTRATOR_DEBUG=1      -> wait for a manual attach (VS Code / Debug &gt; Attach).
    /// </summary>
    private static void RunDebugGateIfRequested()
    {
        var debugMode = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_DEBUG");
        if (debugMode is not ("1" or "true" or "launch"))
        {
            return;
        }

        var name = Assembly.GetEntryAssembly()?.GetName().Name ?? "McpOrchestrator";
        Console.Error.WriteLine(
            $"[{name}] MCP_ORCHESTRATOR_DEBUG={debugMode} — waiting for debugger to attach " +
            $"(PID {Environment.ProcessId})...");

        if (debugMode == "launch" && !Debugger.IsAttached)
        {
            Debugger.Launch();
        }

        while (!Debugger.IsAttached)
        {
            Thread.Sleep(200);
        }

        Console.Error.WriteLine($"[{name}] Debugger attached.");
        Debugger.Break();
    }
}
