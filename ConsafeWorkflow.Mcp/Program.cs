using System.Diagnostics;
using ConsafeWorkflow.Mcp.Workflow;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// Optional debug gate. The IDE launches this server as a child process, so the way to
// debug it is to attach to the spawned process (named "ConsafeWorkflow.Mcp"). This gate
// pauses startup until a debugger attaches, so breakpoints in the tool/engine bind
// before any request is handled. Never write gate output to stdout — that channel is
// reserved for the MCP protocol.
//   CONSAFEWORKFLOW_DEBUG=launch  -> Visual Studio: opens the Just-In-Time debugger
//                                    picker for one-click attach.
//   CONSAFEWORKFLOW_DEBUG=1       -> Wait for a manual attach (use this for VS Code's
//                                    "Attach to ConsafeWorkflow.Mcp" launch config, or
//                                    Visual Studio's Debug > Attach to Process).
var debugMode = Environment.GetEnvironmentVariable("CONSAFEWORKFLOW_DEBUG");
if (debugMode is "1" or "true" or "launch")
{
    Console.Error.WriteLine(
        $"[ConsafeWorkflow.Mcp] CONSAFEWORKFLOW_DEBUG={debugMode} — waiting for debugger to " +
        $"attach (process 'ConsafeWorkflow.Mcp', PID {Environment.ProcessId})...");

    // Only the Visual Studio one-click flow triggers the Windows JIT picker; otherwise we
    // just spin so VS Code (or a manual VS attach) can connect without a VS prompt.
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

// Workflow services. These are the extension seams:
//  - ISessionStore      : swap InMemorySessionStore for a durable store later.
//  - IWorkflowEngine    : replace StubWorkflowEngine with the real state machine.
//  - ILocalModelClient  : register a local OpenAI-compatible client (Ollama/LM Studio).
builder.Services.AddSingleton<ISessionStore, InMemorySessionStore>();
builder.Services.AddSingleton<ILocalModelClient, MockLocalModelClient>();
builder.Services.AddSingleton<IWorkflowEngine, ComponentWorkflowEngine>();

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
