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
    /// <summary>Runs the server over stdio until the host shuts down.</summary>
    public static async Task RunAsync(string[] args)
    {
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
        //  - ICapabilityCatalog          : the address book of downstream MCPs (from config).
        //  - IDownstreamConnectionManager: connects to / proxies calls to those MCPs (MCP client).
        var contentRoot = builder.Environment.ContentRootPath;
        builder.Services.AddSingleton<ICapabilityCatalog>(sp =>
            CapabilityCatalog.Load(
                contentRoot,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CapabilityCatalog>()));
        builder.Services.AddSingleton<IDownstreamConnectionManager, DownstreamConnectionManager>();

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

        await app.RunAsync();
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
