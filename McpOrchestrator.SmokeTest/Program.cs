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

// By default the demo runs the orchestrator via `dotnet run`. Set MCP_ORCHESTRATOR_COMMAND to a
// prebuilt orchestrator binary (e.g. a Native-AOT publish) to drive that instead.
var exeOverride = Environment.GetEnvironmentVariable("MCP_ORCHESTRATOR_COMMAND");

var transport = new StdioClientTransport(new StdioClientTransportOptions
{
    Name = "orchestrator",
    Command = exeOverride ?? "dotnet",
    Arguments = exeOverride is null
        ? new[] { "run", "--project", orchestratorProject, "--no-build" }
        : Array.Empty<string>(),
    WorkingDirectory = solutionDir,
    EnvironmentVariables = new Dictionary<string, string?> { ["MCP_ORCHESTRATOR_CONFIG"] = sampleConfig },
});

await using var client = await McpClient.CreateAsync(transport);

var tools = await client.ListToolsAsync(new RequestOptions());
Console.WriteLine($"\nOrchestrator exposes: {string.Join(", ", tools.Select(t => t.Name))}");

// CI/AOT smoke: confirm the (native) binary starts, serves exactly the 3 meta-tools, and can run
// one — list_capabilities, which exercises the source-generated JSON path that AOT is sensitive to.
// Needs no downstream servers, so it's a fast, reliable gate. Exits with a pass/fail code.
if (args.Contains("--check-tools"))
{
    var names = tools.Select(t => t.Name).ToHashSet();
    string[] expected = ["list_capabilities", "discover_tools", "route"];
    var toolsOk = tools.Count == expected.Length && expected.All(names.Contains);

    var listResult = await client.CallToolAsync("list_capabilities", new Dictionary<string, object?>());
    var text = string.Concat(listResult.Content.OfType<TextContentBlock>().Select(b => b.Text));
    var jsonOk = text.TrimStart().StartsWith('['); // a serialized capability array

    var ok = toolsOk && jsonOk;
    Console.WriteLine(ok
        ? "OK: serves the 3 tools and list_capabilities serialized correctly."
        : $"FAIL: toolsOk={toolsOk} jsonOk={jsonOk}");
    return ok ? 0 : 1;
}

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
return 0;

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
