using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

// End-to-end smoke test for the orchestrator. This console app is itself an MCP *client*:
// it launches the orchestrator over stdio (exactly as an agent's MCP host would) and calls
// its meta-tools. The orchestrator in turn connects to the downstream demo MCP servers.
//
//   smoke-test  ->  McpOrchestrator (orchestrator)  ->  McpOrchestrator.DemoMcp (jira / codegen)
//
// Run after building the solution:  dotnet run --project McpOrchestrator.SmokeTest --no-build

var solutionDir = FindSolutionDir();
var orchestratorProject = Path.Combine(solutionDir, "McpOrchestrator", "McpOrchestrator.csproj");
Console.WriteLine($"Solution dir : {solutionDir}");
Console.WriteLine($"Orchestrator : {orchestratorProject}");

// Point the orchestrator at the repo's sample catalog (jira / codegen / files) so this demo has
// capabilities to route to. The orchestrator otherwise starts empty (no config is checked in at
// the default path) — real users set MCP_ORCHESTRATOR_CONFIG to their own file the same way.
var sampleConfig = Path.Combine(solutionDir, "McpOrchestrator", "orchestrator.config.sample.json");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "orchestrator",
    Command = "dotnet",
    Arguments = ["run", "--project", orchestratorProject, "--no-build"],
    WorkingDirectory = solutionDir,
    EnvironmentVariables = new Dictionary<string, string?> { ["MCP_ORCHESTRATOR_CONFIG"] = sampleConfig },
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

await CallAsync(client, "route", new()
{
    ["capability"] = "codegen",
    ["tool"] = "generate_class",
    ["arguments"] = new Dictionary<string, object?> { ["className"] = "Customer", ["fields"] = "Id, Name, Email" },
});

// --- Real third-party downstream: @modelcontextprotocol/server-filesystem over npx ---
// This capability is NOT our code: it proves cross-vendor interop and a second launch
// path (node/npx, not dotnet) through the same orchestrator seam.
await CallAsync(client, "discover_tools", new() { ["capability"] = "files" });

await CallAsync(client, "route", new()
{
    ["capability"] = "files",
    ["tool"] = "list_directory",
    ["arguments"] = new Dictionary<string, object?> { ["path"] = solutionDir },
});

await CallAsync(client, "route", new()
{
    ["capability"] = "files",
    ["tool"] = "read_text_file",
    ["arguments"] = new Dictionary<string, object?>
    {
        ["path"] = Path.Combine(solutionDir, "McpOrchestrator.slnx"),
    },
});

// Error path: an unknown capability should come back as a structured error, not a crash.
await CallAsync(client, "discover_tools", new() { ["capability"] = "does-not-exist" });

Console.WriteLine("\n=== smoke test complete ===");

// Calls one orchestrator meta-tool and prints any text content blocks from the result.
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

// Walks up from the running assembly to the directory containing the solution file.
static string FindSolutionDir()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpOrchestrator.slnx")))
    {
        dir = dir.Parent;
    }
    return dir?.FullName ?? Directory.GetCurrentDirectory();
}
