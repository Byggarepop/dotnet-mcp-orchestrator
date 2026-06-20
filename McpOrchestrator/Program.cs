using System.Diagnostics;
using McpOrchestrator.Orchestration;
using McpOrchestrator.Orchestration.LocalLlm;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Optional debug gate. The IDE launches this server as a child process, so the way to
// debug it is to attach to the spawned process (named "McpOrchestrator"). This gate
// pauses startup until a debugger attaches, so breakpoints in the tools/orchestration
// bind before any request is handled. Never write gate output to stdout — that channel is
// reserved for the MCP protocol.
//   MCP_ORCHESTRATOR_DEBUG=launch  -> Visual Studio: opens the Just-In-Time debugger picker.
//   MCP_ORCHESTRATOR_DEBUG=1       -> Wait for a manual attach (VS Code / Debug > Attach).
var debugMode = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_DEBUG");
if (debugMode is "1" or "true" or "launch")
{
    Console.Error.WriteLine(
        $"[McpOrchestrator] MCP_ORCHESTRATOR_DEBUG={debugMode} — waiting for debugger to " +
        $"attach (process 'McpOrchestrator', PID {Environment.ProcessId})...");

    if (debugMode == "launch" && !Debugger.IsAttached)
    {
        Debugger.Launch();
    }

    while (!Debugger.IsAttached)
    {
        Thread.Sleep(200);
    }

    Console.Error.WriteLine("[McpOrchestrator] Debugger attached.");
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

// Route planner for the `request` tool. Default: the dependency-free heuristic. Opt in to the
// embedded local LLM with MCP_ORCHESTRATOR_PLANNER=llm — it is always wrapped with the heuristic
// as a fallback, and the (small) model is downloaded lazily on the first `request` call, so
// startup stays fast and offline-friendly.
var llmOptions = LocalLlmOptions.FromEnvironment();
builder.Services.AddSingleton<HeuristicRoutePlanner>();
if (llmOptions.Enabled)
{
    builder.Services.AddSingleton(llmOptions);
    builder.Services.AddSingleton<ModelProvisioner>(sp => new ModelProvisioner(
        sp.GetRequiredService<LocalLlmOptions>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<ModelProvisioner>()));
    builder.Services.AddSingleton<LocalLlm>(sp => new LocalLlm(
        sp.GetRequiredService<LocalLlmOptions>(),
        sp.GetRequiredService<ModelProvisioner>(),
        sp.GetRequiredService<ILoggerFactory>().CreateLogger<LocalLlm>()));
    builder.Services.AddSingleton<IRoutePlanner>(sp => new FallbackRoutePlanner(
        new LlmRoutePlanner(
            sp.GetRequiredService<LocalLlm>(),
            sp.GetRequiredService<ILogger<LlmRoutePlanner>>(),
            llmOptions.ModelFileName),
        sp.GetRequiredService<HeuristicRoutePlanner>(),
        sp.GetRequiredService<ILogger<FallbackRoutePlanner>>()));
}
else
{
    builder.Services.AddSingleton<IRoutePlanner>(sp => sp.GetRequiredService<HeuristicRoutePlanner>());
}

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
