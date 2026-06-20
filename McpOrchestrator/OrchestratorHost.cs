using System.Diagnostics;
using System.Reflection;
using McpOrchestrator.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace McpOrchestrator;

/// <summary>
/// Builds and runs the orchestrator MCP server. Factored out of <c>Program</c> so an alternative
/// host (e.g. the optional <c>McpOrchestrator.LocalLlm</c> package) can reuse the exact same wiring
/// and only swap in a different <see cref="IRoutePlanner"/> — without the core ever depending on
/// the LLM stack.
/// </summary>
public static class OrchestratorHost
{
    /// <summary>
    /// Runs the server over stdio. If <paramref name="configurePlanner"/> is supplied it owns the
    /// <see cref="IRoutePlanner"/> registration; otherwise the dependency-free
    /// <see cref="HeuristicRoutePlanner"/> is used.
    /// </summary>
    public static async Task RunAsync(string[] args, Action<IServiceCollection>? configurePlanner = null)
    {
        RunDebugGateIfRequested();

        var builder = Host.CreateApplicationBuilder(args);

        // stdout is reserved for the MCP stdio protocol — all logging must go to stderr.
        builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

        // Orchestration services:
        //  - ICapabilityCatalog          : the address book of downstream MCPs (from config).
        //  - IDownstreamConnectionManager: connects to / proxies calls to those MCPs (MCP client).
        //  - IRoutePlanner               : interprets a natural-language `request` into a tool call.
        var contentRoot = builder.Environment.ContentRootPath;
        builder.Services.AddSingleton<ICapabilityCatalog>(sp =>
            CapabilityCatalog.Load(
                contentRoot,
                sp.GetRequiredService<ILoggerFactory>().CreateLogger<CapabilityCatalog>()));
        builder.Services.AddSingleton<IDownstreamConnectionManager, DownstreamConnectionManager>();

        // --- Route planner selection (only the `request` tool uses this; `route` needs no planner;
        //     see IRoutePlanner for the full why). There are two roles a planner can play here:
        //
        //   "default"  = the IRoutePlanner the `request` tool actually resolves and calls.
        //   "fallback" = what an LLM planner delegates to when the model isn't ready or errors.
        //
        // The heuristic (keyword matching, no LLM) is registered as a concrete type so it can serve
        // EITHER role. The core host uses it directly as the default; the LLM host (which passes
        // configurePlanner) makes the LLM the default and wires the heuristic in behind it as the
        // fallback. So the heuristic is always available, and the system never depends on an LLM.
        builder.Services.AddSingleton<HeuristicRoutePlanner>();
        if (configurePlanner is not null)
        {
            // An overriding host (e.g. McpOrchestrator.LocalLlm) owns the IRoutePlanner registration.
            configurePlanner(builder.Services);
        }
        else
        {
            // Default: the `request` tool is served by the dependency-free heuristic.
            builder.Services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<HeuristicRoutePlanner>());
        }

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            // Register the meta-tools from the core assembly explicitly, so they are found
            // regardless of which assembly is the process entry point.
            .WithToolsFromAssembly(typeof(Tools.OrchestratorTool).Assembly);

        await builder.Build().RunAsync();
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
