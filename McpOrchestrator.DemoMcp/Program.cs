using McpOrchestrator.DemoMcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

// A sample downstream MCP server. It role-plays as a different service depending on the
// --persona argument, so a single project can stand in for several distinct MCP servers
// (e.g. a "jira" server and a "codegen" server) that the orchestrator routes between.
//
//   dotnet run -- --persona jira       -> exposes get_issue, search_issues
//   dotnet run -- --persona codegen    -> exposes generate_class
//   dotnet run -- --persona diag       -> exposes echo, fail, slow (failure-mode testing)
//   dotnet run                          -> exposes everything (no persona)
var persona = GetArgValue(args, "--persona") ?? "all";

var builder = Host.CreateApplicationBuilder(args);

// stdout is reserved for the MCP stdio protocol — log to stderr only.
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

var mcp = builder.Services.AddMcpServer().WithStdioServerTransport();

switch (persona.ToLowerInvariant())
{
    case "jira":
        mcp.WithTools<JiraTools>();
        break;
    case "codegen":
        mcp.WithTools<CodegenTools>();
        break;
    case "diag":
        mcp.WithTools<DiagnosticsTools>();
        break;
    default:
        mcp.WithTools<JiraTools>().WithTools<CodegenTools>().WithTools<DiagnosticsTools>();
        break;
}

Console.Error.WriteLine($"[DemoMcp] starting with persona '{persona}'.");

await builder.Build().RunAsync();

// Returns the value following the given flag in the argument list, or null if absent.
static string? GetArgValue(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
        {
            return args[i + 1];
        }
    }
    return null;
}
