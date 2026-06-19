# McpOrchestrator — an MCP server that orchestrates other MCP servers

One agent connects to **this single server**; this server holds the connections to many
downstream MCP servers (JIRA, code generation, DB search, a filesystem server, …) and routes
the agent's requests to the right one. Instead of switching agents to switch toolsets, you use
**one agent + one MCP** that can reach everything.

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
                                                          ──MCP──▶  files MCP, db MCP, …
```

---

## Contents

1. [How it works](#how-it-works)
2. [The four tools](#the-four-tools-agent-facing-surface)
3. [Prerequisites](#prerequisites)
4. [Build & run the demo](#build--run-the-demo)
5. [Register the orchestrator with an agent](#register-the-orchestrator-with-an-agent)
6. [Add a new downstream MCP](#add-a-new-downstream-mcp)
7. [Configuration reference](#configuration-reference)
8. [Testing](#testing)
9. [Troubleshooting & pitfalls](#troubleshooting--pitfalls)
10. [Security](#security)
11. [Extending](#extending)
12. [Project layout](#project-layout)
13. [Debugging](#debugging)

---

## How it works

1. The agent asks the orchestrator **what it can reach** (`list_capabilities`) — the catalog
   comes from config, so each capability has a name, a summary, and usage instructions.
2. The agent reads each capability's **instructions**, calls `discover_tools` to see the exact
   tools and their JSON schemas, then `route`s a specific tool with arguments it fills in itself.
3. The orchestrator **connects to that downstream MCP** (launching it on first use), invokes the
   tool, and **relays the result back** to the agent.

> ### Design principle: the orchestrator is a courier, not an interpreter
> It forwards exactly what the agent sends and does no language understanding of its own. The
> interpreting is the LLM's job — which is why each capability's `instructions` spell out the
> precise arguments to pass (e.g. *"always include the Jira issue key as
> `{\"issueKey\":\"PROJ-123\"}`"*). This makes the system reliable for tasks that need no
> interpretation by the orchestrator. The `request` tool (orchestrator-side guessing) exists
> only as a best-effort convenience — see [Why `route` over `request`](#why-route-over-request).

Connections are made **lazily** on first use, **cached** per capability for reuse, and
**disposed** on shutdown. A connect that fails or times out is **evicted** so the next call
retries instead of awaiting a dead connection.

---

## The four tools (agent-facing surface)

| Tool | Parameters | Purpose |
| --- | --- | --- |
| `list_capabilities` | — | List the downstream MCPs (name, summary, instructions). Call first. |
| `discover_tools` | `capability` | Connect to one capability and list its tools + input schemas. |
| `route` | `capability`, `tool`, `arguments` | **Preferred.** Forward a specific tool call (you pick the tool and fill the arguments) and return its result. |
| `request` | `capability`, `request` | *Best-effort convenience.* Describe a need in words; the orchestrator **guesses** the tool/args with a keyword heuristic. Unreliable — prefer `route`. |

`route`/`request` return JSON: `capability`, `tool`, `isError`, `text` (flattened text
content), `structured` (when the downstream tool returns structured content), the `arguments`
actually sent (echoed for auditing), and — for `request` — a `rationale` for the choice.
Anything that goes wrong is returned as `{ "error": ..., "availableCapabilities": [...] }`
rather than thrown, so the agent always receives parseable JSON.

### Why `route` over `request`

`request` asks the orchestrator to turn a sentence into a tool call. The shipped
`HeuristicRoutePlanner` does this with keyword matching and a couple of regexes — it has **no
language understanding**, so it only works when the tool is obvious and the request literally
contains the argument values (e.g. an explicit `PROJ-123` key). Give it
*"generate a class named Customer with fields Id, Name, Email"* and it dumps the whole sentence
into `className`. The capable interpreter is already in the loop — the agent's LLM — so the
reliable pattern is `discover_tools` + `route`, with the model filling the arguments per each
capability's instructions. Treat `request` as a shortcut for trivial cases only. (This weakness
is pinned down by a characterization test in the test suite.)

---

## Prerequisites

- **.NET SDK 10** (`dotnet --version` ≥ `10.0.300`).
- **Node.js / npx** — only if you point a capability at an npm-based MCP server (e.g. the
  filesystem reference server). `node` ≥ 18.
- The repo ships a local [`NuGet.config`](../NuGet.config) that restores from nuget.org alone,
  so a clean `dotnet build` works without any machine-level feed.

---

## Build & run the demo

This repo has four projects:

- **`McpOrchestrator`** — the orchestrator (this README).
- **`McpOrchestrator.DemoMcp`** — a sample downstream MCP that role-plays as `jira`, `codegen`,
  or `diag` via `--persona` (so one project stands in for several distinct servers).
- **`McpOrchestrator.SmokeTest`** — a console MCP client that drives the orchestrator
  end-to-end (also a copy-paste usage example).
- **`McpOrchestrator.Tests`** — the xUnit test suite.

```bash
# Build everything once (IDE registration and the demo use --no-build).
dotnet build McpOrchestrator.slnx

# Run the end-to-end demo: smoke-test → orchestrator → demo MCP (jira + codegen + files).
dotnet run --project McpOrchestrator.SmokeTest --no-build
```

The orchestrator speaks **stdio** (JSON-RPC). All logging goes to **stderr**; stdout is
reserved for the MCP protocol.

---

## Register the orchestrator with an agent

> **Critical: the catalog and the tool descriptions are read once, at server startup.** After
> you change `orchestrator.config.json` or rebuild the server, the agent host must **restart the
> MCP server** (usually: restart the IDE / Claude Code session) to pick up the change. See
> [Troubleshooting](#troubleshooting--pitfalls).

### Claude Code

Claude Code reads MCP servers from the **`mcpServers`** key (not `servers`). The simplest, most
robust way to register is the CLI, which writes to your local Claude config and leaves the
repo's IDE files untouched:

```bash
dotnet build McpOrchestrator.slnx
claude mcp add orchestrator --scope local -- \
  dotnet run --project /absolute/path/to/McpOrchestrator/McpOrchestrator.csproj --no-build
claude mcp list      # should show: orchestrator … ✔ Connected
```

Then **restart the Claude Code session** so the tools load. Ask in plain language
(*"list the repo files"*, *"status of PROJ-3"*) and the model will `list_capabilities` → pick a
capability → `route`.

### Visual Studio 2022/2026 and VS Code

Register a stdio server in [`.mcp.json`](../.mcp.json) (Visual Studio) and
[`.vscode/mcp.json`](../.vscode/mcp.json) (VS Code). These use the **`servers`** key:

```json
{
  "servers": {
    "orchestrator": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["run", "--project", "${workspaceFolder}/McpOrchestrator/McpOrchestrator.csproj", "--no-build"]
    }
  }
}
```

- **Visual Studio does *not* expand `${workspaceFolder}`** — use an **absolute** `--project`
  path in `.mcp.json`. `${workspaceFolder}` is fine in `.vscode/mcp.json` (VS Code).
- Open **`McpOrchestrator.slnx`** (or the repo root), not a bare `.csproj`, so the IDE discovers
  the repo-root MCP config.
- Reference the tools from an agent as `orchestrator/*` — see
  [`.github/agents/McpOrchestrator-Example.agent.md`](../.github/agents/McpOrchestrator-Example.agent.md).

---

## Add a new downstream MCP

Adding a capability is a **config-only** change — no code. Edit
[`orchestrator.config.json`](orchestrator.config.json) and add one entry to `capabilities`.

**Step 1 — describe the connection and the instructions.**

```jsonc
{
  "name": "github",                              // short id the agent addresses (unique)
  "summary": "GitHub — issues, PRs, code search.",
  "instructions": "Use for repository questions. Call 'discover_tools' first; always pass owner and repo, e.g. {\"owner\":\"octocat\",\"repo\":\"hello\"}.",
  "enabled": true,
  "transport": "stdio",                          // only stdio is supported
  "command": "npx",                              // executable to launch the downstream server
  "args": ["-y", "@modelcontextprotocol/server-github"],
  "env": { "GITHUB_PERSONAL_ACCESS_TOKEN": "${GITHUB_TOKEN}" },
  "connectTimeoutSeconds": 90                    // optional; npx first-run can be slow
}
```

**Step 2 — write good `instructions`.** This is the most important field. Because the
orchestrator is a courier, the agent relies entirely on these instructions to know **which tool
to call and exactly what arguments to pass**. Be specific and prescriptive:
*"call `get_issue` and ALWAYS pass `{\"issueKey\":\"PROJ-123\"}`; never pass a sentence."*

**Step 3 — restart the agent host** (the catalog loads at startup), then verify:
`list_capabilities` should now show your capability, and `discover_tools` should list its tools.

### Worked example: a real third-party server (no code you wrote)

The shipped config includes a `files` capability backed by the official
`@modelcontextprotocol/server-filesystem` reference server, launched via `npx`:

```jsonc
{
  "name": "files",
  "summary": "Filesystem access — list, read and search files under the repository root.",
  "instructions": "Call 'discover_tools' first — common tools: 'list_directory' {\"path\":\"<dir>\"}, 'read_text_file' {\"path\":\"<file>\"}. ALWAYS pass an absolute path within the allowed root.",
  "enabled": true,
  "transport": "stdio",
  "command": "npx",
  "args": ["-y", "@modelcontextprotocol/server-filesystem", "${SOLUTION_DIR}"],
  "workingDirectory": "${SOLUTION_DIR}"
}
```

To **temporarily disable** a capability without deleting it, set `"enabled": false`.

---

## Configuration reference

Each entry in `capabilities` is one downstream MCP server.

| Field | Required | Default | Meaning |
| --- | --- | --- | --- |
| `name` | yes | — | Short, unique id the agent uses to address the capability. Matched case-insensitively. Duplicates are ignored (first wins). |
| `summary` | recommended | `""` | One-line description shown to the model. |
| `instructions` | recommended | `""` | Prescriptive usage guidance: which tool, what arguments. The courier relies on this. |
| `enabled` | no | `true` | When `false`, the capability is skipped entirely. |
| `transport` | no | `"stdio"` | Only `stdio` is implemented. |
| `command` | yes | — | Executable that launches the downstream server (e.g. `dotnet`, `npx`). An entry with no command is skipped. |
| `args` | no | `[]` | Arguments to `command`. Supports `${VAR}` substitution. |
| `workingDirectory` | no | host cwd | Working directory for the spawned server. Supports `${VAR}`. |
| `env` | no | `{}` | Extra environment variables for the spawned server. Values support `${VAR}`. |
| `connectTimeoutSeconds` | no | `60` | Deadline for launch + MCP handshake. A timeout faults the connect and evicts it. |
| `callTimeoutSeconds` | no | `100` | Deadline for a single tool call / tool-list. A timeout faults the call but keeps the connection. |

**`${VAR}` substitution** (in `command`, `args`, `workingDirectory`, and `env` values):

- `${SOLUTION_DIR}` — the repository root (built in).
- `${CONFIG_DIR}` — the folder containing the config file (built in).
- any other `${NAME}` — resolved from a **process environment variable**; an unresolved token
  is left as-is and logged.

**Config location** is resolved in this order, first hit wins:

1. `MCP_ORCHESTRATOR_CONFIG` environment variable (an explicit file path), then
2. `<solutionDir>/McpOrchestrator/orchestrator.config.json` (the in-repo source), then
3. `orchestrator.config.json` next to the built assembly, then
4. `orchestrator.config.json` in the host content root.

A missing or invalid config is non-fatal: the server starts with **zero capabilities** and logs
a warning/error rather than crashing.

---

## Testing

```bash
dotnet test McpOrchestrator.slnx
```

The `McpOrchestrator.Tests` project (xUnit) has 37 tests:

- **Unit** — catalog validation and dedup, `${VAR}` substitution, invalid-JSON resilience;
  argument parsing (object / JSON-string / scalar / array / null / omitted); planner behavior
  including a characterization test of its sentence-into-argument weakness.
- **Integration** — against the real demo server as a live downstream process: connect + list,
  call a tool, unknown capability, downstream failure surfaced as `isError`, **call timeout**,
  **connect timeout**, bad-command eviction, and 20-way concurrency on one cached connection.
- **End-to-end** — the four tool methods driven through the full catalog → connection manager →
  downstream path, covering happy and error paths.

Integration tests launch the compiled `McpOrchestrator.DemoMcp.dll` directly, so build the
solution first (the test project references it, so a normal `dotnet test` does this for you).

---

## Troubleshooting & pitfalls

| Symptom | Cause & fix |
| --- | --- |
| **Edited the config / rebuilt, but the agent still sees the old behavior.** | The catalog and tool descriptions load **once at startup**. Restart the MCP server (restart the IDE / Claude Code session). |
| **Build fails with `MSB3027`/`MSB3021` "file is being used by another process".** | A running orchestrator (launched by the IDE/Claude as a child process) locks its own `.exe`/`.dll`. Stop that server before rebuilding, then restart it. `--no-build` launches never refresh a stale build. |
| **Claude Code says `Missing "mcpServers" — found "servers"`.** | `.mcp.json` uses the Visual Studio `servers` schema. Claude Code needs `mcpServers`. Don't edit the VS file — register separately with `claude mcp add` (local scope). |
| **Visual Studio can't find the project / config.** | VS does not expand `${workspaceFolder}` in `.mcp.json` — use an absolute `--project` path. Open the `.slnx`, not a bare `.csproj`. |
| **A capability times out on first call.** | First-run `npx` downloads the package, which can exceed the 60s connect default. Raise `connectTimeoutSeconds` for that capability. |
| **`list_capabilities` is empty.** | The config wasn't found or failed to parse, or every entry was disabled / missing a `command`. Check stderr — loading logs the count, the path, and any parse error. |
| **Garbled / wrong arguments via `request`.** | Expected — the heuristic planner has no language understanding. Use `discover_tools` + `route`. |
| **Nothing appears on stdout / the protocol breaks.** | Never write to stdout from a server; it's reserved for MCP. All diagnostics go to stderr. |

---

## Security

The orchestrator **launches arbitrary executables** named in its config (`command`/`args`/`env`)
and substitutes environment variables into them. Treat `orchestrator.config.json` as **trusted
input**: only add capabilities you control, and be deliberate about which `${VAR}` (and therefore
which environment secrets) you expose to a downstream process. Downstream tool *results* are data
— the orchestrator relays them verbatim and does not execute them.

---

## Extending

- **Real routing intelligence** — `request` selects a tool via `IRoutePlanner`. The shipped
  `HeuristicRoutePlanner` is dependency-free keyword matching; swap in an LLM-backed planner
  (e.g. a local Ollama / LM Studio model) for production-quality selection. The planner's core is
  isolated behind `HeuristicRoutePlanner.ToolSpec`, so an alternative is easy to slot in.
- **More transports** — `DownstreamConnectionManager` implements only `stdio`; add HTTP/SSE
  (the SDK ships an HTTP client transport) by branching on `descriptor.Transport`.
- **Connection lifecycle** — lazy connect, per-capability cache, fault eviction, and timeouts are
  already in place; new transports should reuse the same `GetClientAsync` path.

---

## Project layout

```
McpOrchestrator/
  Program.cs                              Host + MCP stdio server wiring (DI of the services below)
  orchestrator.config.json               The downstream catalog (connections + instructions)
  Tools/OrchestratorTool.cs              The 4 meta-tools: list_capabilities/discover_tools/route/request
  Orchestration/
    CapabilityDescriptor.cs              Config POCO: one downstream MCP (+ OrchestratorConfig root)
    ICapabilityCatalog.cs                The address book of downstream capabilities
    CapabilityCatalog.cs                 Loads + validates the catalog from JSON; resolves ${VAR} tokens
    IDownstreamConnectionManager.cs      Contract: list/call downstream tools (+ CapabilityNotFoundException)
    DownstreamConnectionManager.cs       MCP client: lazy connect, cache, timeouts, proxy, dispose
    IRoutePlanner.cs                     NL request → (tool, arguments) seam (RoutePlan)
    HeuristicRoutePlanner.cs             Dependency-free planner (keyword match + arg extraction)
    ToolPayloads.cs                      Pure argument/result conversions (unit-tested)
    RoutingModels.cs                     DTOs returned to the agent (+ JSON options)

McpOrchestrator.DemoMcp/                 Sample downstream MCP (personas: jira / codegen / diag)
McpOrchestrator.SmokeTest/               Console MCP client that drives the orchestrator
McpOrchestrator.Tests/                   xUnit suite (unit + integration + end-to-end)
```

---

## Debugging

The server is launched by the IDE's MCP host, so debug it by **attaching to the spawned process**
(named `McpOrchestrator`). A startup gate pauses until a debugger attaches: set
`MCP_ORCHESTRATOR_DEBUG=launch` (Visual Studio JIT picker) or `=1` (manual attach) in the server's
`env` block. The PID is logged to stderr on startup. Remove the env var when done.
