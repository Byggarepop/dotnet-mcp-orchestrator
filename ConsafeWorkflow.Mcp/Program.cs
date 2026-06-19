using System.Diagnostics;
using ConsafeWorkflow.Mcp.Orchestration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Optional debug gate. The IDE launches this server as a child process, so the way to
// debug it is to attach to the spawned process (named "ConsafeWorkflow.Mcp"). This gate
// pauses startup until a debugger attaches, so breakpoints in the tools/orchestration
// bind before any request is handled. Never write gate output to stdout — that channel is
// reserved for the MCP protocol.
//   CONSAFEWORKFLOW_DEBUG=launch  -> Visual Studio: opens the Just-In-Time debugger picker.
//   CONSAFEWORKFLOW_DEBUG=1       -> Wait for a manual attach (VS Code / Debug > Attach).
var debugMode = Environment.GetEnvironmentVariable("CONSAFEWORKFLOW_DEBUG");
if (debugMode is "1" or "true" or "launch")
{
    Console.Error.WriteLine(
        $"[ConsafeWorkflow.Mcp] CONSAFEWORKFLOW_DEBUG={debugMode} — waiting for debugger to " +
        $"attach (process 'ConsafeWorkflow.Mcp', PID {Environment.ProcessId})...");

    if (debugMode == "launch" && !Debugger.IsAttached)
    {
        Debugger.Launch();
    }

    while (!Debugger.IsAttached)
    {
        Thread.Sleep(200);
    }

    Console.Error.WriteLine("[ConsafeWorkflow.Mcp] Debugger attached.");
    Debugger.Break();
}

var builder = Host.CreateApplicationBuilder(args);

// stdout is reserved for the MCP stdio protocol — all logging must go to stderr.
builder.Logging.AddConsole(options =>
{
    options.LogToStandardErrorThreshold = LogLevel.Trace;
});

// Orchestration services:
//  - ICapabilityCatalog          : the address book of downstream MCPs (from config).
//  - IDownstreamConnectionManager: connects to / proxies calls to those MCPs (MCP client).
//  - IRoutePlanner               : natural-language need -> concrete tool call (LLM seam).
var contentRoot = builder.Environment.ContentRootPath;
builder.Services.AddSingleton<ICapabilityCatalog>(sp =>
    CapabilityCatalog.Load(
        contentRoot,
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<CapabilityCatalog>()));
builder.Services.AddSingleton<IDownstreamConnectionManager, DownstreamConnectionManager>();
builder.Services.AddSingleton<IRoutePlanner, HeuristicRoutePlanner>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
