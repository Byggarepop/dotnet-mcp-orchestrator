using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// End-to-end smoke test for the orchestrator. This console app is itself an MCP *client*:
// it launches the orchestrator over stdio (exactly as an agent's MCP host would) and calls
// its meta-tools. The orchestrator in turn connects to the downstream demo MCP servers.
//
//   smoke-test  ->  ConsafeWorkflow.Mcp (orchestrator)  ->  ConsafeWorkflow.DemoMcp (jira / codegen)
//
// Run after building the solution:  dotnet run --project ConsafeWorkflow.SmokeTest --no-build

var solutionDir = FindSolutionDir();
var orchestratorProject = Path.Combine(solutionDir, "ConsafeWorkflow.Mcp", "ConsafeWorkflow.Mcp.csproj");
Console.WriteLine($"Solution dir : {solutionDir}");
Console.WriteLine($"Orchestrator : {orchestratorProject}");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "orchestrator",
    Command = "dotnet",
    Arguments = ["run", "--project", orchestratorProject, "--no-build"],
    WorkingDirectory = solutionDir,
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync(new RequestOptions());
Console.WriteLine($"\nOrchestrator exposes: {string.Join(", ", tools.Select(t => t.Name))}");

await CallAsync(client, "list_capabilities", new());

await CallAsync(client, "discover_tools", new() { ["capability"] = "jira" });

await CallAsync(client, "route", new()
{
    ["capability"] = "jira",
    ["tool"] = "get_issue",
    ["arguments"] = new Dictionary<string, object?> { ["issueKey"] = "PROJ-1" },
});

await CallAsync(client, "request", new()
{
    ["capability"] = "jira",
    ["request"] = "what is the status of PROJ-3?",
});

await CallAsync(client, "route", new()
{
    ["capability"] = "codegen",
    ["tool"] = "generate_class",
    ["arguments"] = new Dictionary<string, object?> { ["className"] = "Customer", ["fields"] = "Id, Name, Email" },
});

// Error path: an unknown capability should come back as a structured error, not a crash.
await CallAsync(client, "discover_tools", new() { ["capability"] = "does-not-exist" });

Console.WriteLine("\n=== smoke test complete ===");

static async Task CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
{
    Console.WriteLine($"\n=== {tool}({JsonSerializer.Serialize(arguments)}) ===");
    var result = await client.CallToolAsync(tool, arguments);
    foreach (var block in result.Content)
    {
        if (block is TextContentBlock text)
        {
            Console.WriteLine(text.Text);
        }
    }
    if (result.IsError == true)
    {
        Console.WriteLine("(tool reported IsError = true)");
    }
}

static string FindSolutionDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ConsafeWorkflow.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
