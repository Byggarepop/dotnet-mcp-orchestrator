# ConsafeWorkflow.Mcp — MCP Orchestrator

An **MCP server that orchestrates other MCP servers.** One agent connects to this single
server; this server holds the connections to many downstream MCP servers (JIRA, code
generation, DB search, …) and routes the agent's requests to the right one. So instead of
switching agents to switch toolsets, you use **one agent + one MCP** that reaches everything.

The orchestrator is therefore both:

- an **MCP server** to the agent (it exposes the four meta-tools below), and
- an **MCP client** to each downstream server (it launches them and forwards tool calls).

```
                         ┌─────────────────────── orchestrator (this server) ───────────────────────┐
   one agent  ──MCP──▶   │  list_capabilities · discover_tools · route · request                     │
   (the model)           │        │ catalog (config)            │ connection manager (MCP client)    │
                         └────────┼─────────────────────────────┼────────────────────────────────────┘
                                  │                              │
                          orchestrator.config.json       ──MCP──▶  jira MCP      (get_issue, search_issues)
                          (connections + instructions)   ──MCP──▶  codegen MCP   (generate_class)
                                                          ──MCP──▶  db MCP, …
```

## The flow

1. The agent asks the orchestrator **what it can reach** (`list_capabilities`) — the catalog
   comes from config, so each capability has a name, a summary, and usage instructions.
2. The agent expresses **what it needs** — either precisely (`route` a specific downstream
   tool) or in plain language (`request`, and the orchestrator picks the tool).
3. The orchestrator **connects to that downstream MCP**, invokes the tool, and **relays the
   result back** to the agent.

## The tools (agent-facing surface)

| Tool | Parameters | Purpose |
| --- | --- | --- |
| `list_capabilities` | — | List the downstream MCPs (name, summary, instructions). Call first. |
| `discover_tools` | `capability` | Connect to one capability and list its tools + input schemas. |
| `route` | `capability`, `tool`, `arguments` | Forward a specific tool call and return its result. |
| `request` | `capability`, `request` | Describe a need in words; the orchestrator picks the tool/args. |

`route`/`request` return JSON: `text` (flattened text content), `structured` (when the
downstream tool provides structured content), the `arguments` actually sent, and — for
`request` — a `rationale` for the choice. Errors are returned as
`{ "error": ..., "availableCapabilities": [...] }` rather than thrown.

## Configuring downstream MCPs

The catalog lives in [`orchestrator.config.json`](orchestrator.config.json). Each entry is
one downstream MCP server — the **connection** (`command`/`args`/`workingDirectory`/`env`)
plus the **instructions** the model sees (`summary`/`instructions`):

```jsonc
{
  "capabilities": [
    {
      "name": "jira",
      "summary": "Issue tracking — read and search JIRA tickets.",
      "instructions": "Use for tickets/issues/sprint status. Include the issue key when known.",
      "enabled": true,
      "transport": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${SOLUTION_DIR}/ConsafeWorkflow.DemoMcp/ConsafeWorkflow.DemoMcp.csproj", "--no-build", "--", "--persona", "jira"],
      "workingDirectory": "${SOLUTION_DIR}"
    }
  ]
}
```

- **Tokens:** `${SOLUTION_DIR}` (repo root) and `${CONFIG_DIR}` (this file's folder) are
  built in; any other `${VAR}` resolves from an environment variable.
- **Transport:** only `stdio` is implemented in this prototype.
- **Override path:** set `CONSAFE_ORCHESTRATOR_CONFIG` to point at a different config file.

### Pointing at a *real* downstream MCP

Replace the demo entries with real servers. Any stdio MCP server works — for example an
npm-based one:

```jsonc
{
  "name": "github",
  "summary": "GitHub — issues, PRs, code search.",
  "instructions": "Use for repository questions. Provide owner/repo when known.",
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@modelcontextprotocol/server-github"],
  "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "${GITHUB_TOKEN}" }
}
```

## Build and run

This repo has three projects:

- **`ConsafeWorkflow.Mcp`** — the orchestrator (this README).
- **`ConsafeWorkflow.DemoMcp`** — a sample downstream MCP that role-plays as `jira` or
  `codegen` via `--persona` (so one project stands in for several distinct servers).
- **`ConsafeWorkflow.SmokeTest`** — a console MCP client that drives the orchestrator
  end-to-end (also a usage example).

```bash
# build everything once (the IDE registration and config use --no-build)
dotnet build ConsafeWorkflow.slnx

# run the end-to-end demo: smoke-test → orchestrator → demo MCP (jira + codegen)
dotnet run --project ConsafeWorkflow.SmokeTest --no-build
```

The orchestrator speaks **stdio** (JSON-RPC). All logging goes to **stderr**; stdout is
reserved for the MCP protocol.

## IDE integration

The orchestrator is registered for Visual Studio / VS Code as the stdio server
**`consafeworkflow`** in [`.mcp.json`](../.mcp.json) and
[`.vscode/mcp.json`](../.vscode/mcp.json) (it launches `ConsafeWorkflow.Mcp.csproj` with
`--no-build`, so build once first). Reference its tools from an agent as `consafeworkflow/*`
— see [`.github/agents/ConsafeWorkflow-Example.agent.md`](../.github/agents/ConsafeWorkflow-Example.agent.md).
Rename the server id to `orchestrator` in those files if you prefer (keep the agent's
`tools:` line in sync).

> Open **`ConsafeWorkflow.slnx`** (or the repo root), not a bare `.csproj`, so the IDE
> discovers the repo-root MCP config. Stop the IDE's server before rebuilding (it locks the
> output exe).

## Designed for extension

- **Real routing intelligence** — `request` selects a downstream tool via `IRoutePlanner`.
  The shipped `HeuristicRoutePlanner` does keyword matching + simple argument extraction with
  no dependencies; swap in an LLM-backed planner (e.g. a local Ollama / LM Studio model) for
  production-quality selection. This is the natural place a local model plugs in.
- **More transports** — `DownstreamConnectionManager` only implements `stdio`; add
  HTTP/SSE (the SDK ships `HttpClientTransport`) alongside it.
- **Connection lifecycle** — connections are made lazily on first use, cached per capability,
  and disposed on shutdown. A failed connect is evicted so the next call retries.

## Project layout

```
ConsafeWorkflow.Mcp/
  Program.cs                              Host + MCP stdio server wiring (DI of the services below)
  orchestrator.config.json               The downstream catalog (connections + instructions)
  Tools/OrchestratorTool.cs              The 4 meta-tools: list_capabilities/discover_tools/route/request
  Orchestration/
    CapabilityDescriptor.cs              Config POCO: one downstream MCP (+ OrchestratorConfig root)
    ICapabilityCatalog.cs                The address book of downstream capabilities
    CapabilityCatalog.cs                 Loads the catalog from JSON; resolves ${VAR} tokens
    IDownstreamConnectionManager.cs      Contract: list/call downstream tools (+ CapabilityNotFoundException)
    DownstreamConnectionManager.cs       MCP client: lazy connect, cache, proxy, dispose
    IRoutePlanner.cs                     NL request → (tool, arguments) seam (RoutePlan)
    HeuristicRoutePlanner.cs             Dependency-free planner (keyword match + arg extraction)
    RoutingModels.cs                     DTOs returned to the agent (+ JSON options)
```

## Debugging

The server is launched by the IDE's MCP host, so debug it by **attaching to the spawned
process** (named `ConsafeWorkflow.Mcp`). A startup gate pauses until a debugger attaches:
set `CONSAFEWORKFLOW_DEBUG=launch` (Visual Studio JIT picker) or `=1` (manual attach) in the
server's `env` block. The PID is logged to stderr on startup. Remove the env var when done.
