// Interop probe: can Microsoft Agent Framework's MCP skills discovery
// (Microsoft.Agents.AI.Mcp, the consumer side of SEP-2640) discover and load
// the skills served by McpOrchestrator (the producer side)?
//
// Level 1 talks raw MCP: reads the well-known skill://index.json catalog
// resource and every SKILL.md it references.
// Level 2 goes through Agent Framework's AgentSkillsProviderBuilder.UseMcpSkills
// and asks the provider for the context it would hand an LLM agent.
//
// Prereq: build the orchestrator first (Release — Debug bins may be locked):
//   dotnet build McpOrchestrator -c Release

using System.Reflection;
using System.Text.Json;
using Microsoft.Agents.AI;
using ModelContextProtocol.Client;

var repoRoot = FindRepoRoot();
var orchestratorDll = Path.Combine(repoRoot, "McpOrchestrator", "bin", "Release", "net10.0", "McpOrchestrator.dll");
var configPath = Path.Combine(repoRoot, "McpOrchestrator", "orchestrator.config.sample.json");

if (!File.Exists(orchestratorDll))
{
    Console.Error.WriteLine($"FAIL: orchestrator not built: {orchestratorDll}");
    Console.Error.WriteLine("Run: dotnet build McpOrchestrator -c Release");
    return 1;
}

Console.WriteLine($"Spawning orchestrator: dotnet {orchestratorDll}");
await using var client = await McpClient.CreateAsync(
    new StdioClientTransport(new()
    {
        Name = "mcp-orchestrator",
        Command = "dotnet",
        Arguments = [orchestratorDll],
        EnvironmentVariables = new Dictionary<string, string?>
        {
            ["MCP_ORCHESTRATOR_CONFIG"] = configPath,
        },
    }));

Console.WriteLine($"Connected. Server: {client.ServerInfo.Name} {client.ServerInfo.Version}");
Console.WriteLine();

// ---------- Level 1: raw MCP — the wire format the blog's discovery relies on ----------
Console.WriteLine("=== Level 1: raw MCP resources ===");
var resources = await client.ListResourcesAsync();
Console.WriteLine($"resources/list returned {resources.Count} resources:");
foreach (var r in resources)
{
    Console.WriteLine($"  {r.Uri}  ({r.MimeType})");
}

var indexResult = await client.ReadResourceAsync("skill://index.json");
var indexText = indexResult.Contents
    .OfType<ModelContextProtocol.Protocol.TextResourceContents>()
    .Single().Text;
Console.WriteLine();
Console.WriteLine("skill://index.json:");
Console.WriteLine(indexText);

using var index = JsonDocument.Parse(indexText);
var indexedSkills = new List<(string Name, string Url)>();
foreach (var entry in index.RootElement.GetProperty("skills").EnumerateArray())
{
    indexedSkills.Add((entry.GetProperty("name").GetString()!, entry.GetProperty("url").GetString()!));
}

Console.WriteLine();
foreach (var (name, url) in indexedSkills)
{
    var skillMd = await client.ReadResourceAsync(url);
    var text = skillMd.Contents
        .OfType<ModelContextProtocol.Protocol.TextResourceContents>()
        .Single().Text;
    var firstLines = string.Join(" | ", text.Split('\n').Take(4).Select(l => l.TrimEnd()));
    Console.WriteLine($"  read {url}: {text.Length} chars — {firstLines}");
}

if (indexedSkills.Count == 0)
{
    Console.Error.WriteLine("FAIL: index.json listed no skills.");
    return 1;
}

Console.WriteLine($"Level 1 OK: index lists {indexedSkills.Count} skills, all SKILL.md files readable.");
Console.WriteLine();

// ---------- Level 2: Agent Framework's UseMcpSkills against the same client ----------
Console.WriteLine("=== Level 2: Microsoft.Agents.AI.Mcp UseMcpSkills ===");
using var skillsProvider = new AgentSkillsProviderBuilder()
    .UseMcpSkills(client)
    .Build();

var discovered = new List<AgentSkill>();
var stubAgent = new StubAgent();
var stubSession = await stubAgent.CreateSessionAsync();

// Drive the provider the way an agent run would, no LLM required.
var context = await skillsProvider.InvokingAsync(
    new AIContextProvider.InvokingContext(stubAgent, stubSession, new AIContext()), CancellationToken.None);
Console.WriteLine("Provider AIContext.Instructions:");
Console.WriteLine(context.Instructions ?? "(null)");
if (context.Tools is { } tools)
{
    Console.WriteLine($"Provider exposes {tools.Count()} tools: {string.Join(", ", tools.Select(t => t.Name))}");
}

// Ground truth: pull the provider's composed AgentSkillsSource and enumerate it.
var sourceField = skillsProvider.GetType()
    .GetFields(BindingFlags.NonPublic | BindingFlags.Instance)
    .FirstOrDefault(f => typeof(AgentSkillsSource).IsAssignableFrom(f.FieldType));
if (sourceField?.GetValue(skillsProvider) is AgentSkillsSource source)
{
    var skills = await source.GetSkillsAsync(new AgentSkillsSourceContext(stubAgent, stubSession), CancellationToken.None);
    discovered.AddRange(skills);
}
else
{
    Console.Error.WriteLine("FAIL: could not locate the provider's AgentSkillsSource.");
    return 1;
}

Console.WriteLine($"Agent Framework discovered {discovered.Count} skills from the orchestrator:");
foreach (var skill in discovered)
{
    var content = await skill.GetContentAsync(CancellationToken.None);
    Console.WriteLine($"  {skill.Frontmatter.Name}: {skill.Frontmatter.Description}");
    Console.WriteLine($"    SKILL.md content loaded: {content.Length} chars");
}

// ---------- Verdict ----------
Console.WriteLine();
var indexNames = indexedSkills.Select(s => s.Name).OrderBy(n => n).ToList();
var frameworkNames = discovered.Select(s => s.Frontmatter.Name).OrderBy(n => n).ToList();
if (frameworkNames.Count > 0 && indexNames.SequenceEqual(frameworkNames))
{
    Console.WriteLine($"INTEROP OK — Agent Framework sees the same {frameworkNames.Count} skills the orchestrator publishes: {string.Join(", ", frameworkNames)}");
    return 0;
}

Console.Error.WriteLine($"FAIL: index has [{string.Join(", ", indexNames)}] but Agent Framework found [{string.Join(", ", frameworkNames)}]");
return 1;

// Walks up from the build output directory to the repo root, so the probe runs
// from any checkout location. MCP_REPO_ROOT overrides for out-of-tree runs.
static string FindRepoRoot()
{
    if (Environment.GetEnvironmentVariable("MCP_REPO_ROOT") is { Length: > 0 } overridden)
    {
        return overridden;
    }

    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "McpOrchestrator.slnx")))
    {
        dir = dir.Parent;
    }

    return dir?.FullName
        ?? throw new InvalidOperationException(
            $"Could not find McpOrchestrator.slnx above {AppContext.BaseDirectory}; set MCP_REPO_ROOT.");
}

/// <summary>No-LLM stand-in agent: the skills provider only needs a non-null agent/session pair.</summary>
sealed class StubAgent : AIAgent
{
    protected override ValueTask<AgentSession> CreateSessionCoreAsync(CancellationToken cancellationToken = default)
        => new(new StubSession());

    protected override ValueTask<JsonElement> SerializeSessionCoreAsync(AgentSession session, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override ValueTask<AgentSession> DeserializeSessionCoreAsync(JsonElement serializedSession, JsonSerializerOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override Task<AgentResponse> RunCoreAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    protected override IAsyncEnumerable<AgentResponseUpdate> RunCoreStreamingAsync(IEnumerable<Microsoft.Extensions.AI.ChatMessage> messages, AgentSession? session = null, AgentRunOptions? options = null, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();

    sealed class StubSession : AgentSession;
}
