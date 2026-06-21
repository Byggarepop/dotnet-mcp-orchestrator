# McpOrchestrator — a .NET-native MCP orchestrator

**Route one agent through one server to many MCP servers, with progressive tool discovery to keep
the agent's context small.**

One agent connects to **this single server**; it holds the connections to many downstream MCP
servers (JIRA, code generation, DB search, a filesystem server, …) and relays the agent's calls to
the right one. Instead of switching agents to switch toolsets, you use **one agent + one MCP** that
can reach everything — and because the downstream tools are discovered on demand, the agent's
always-loaded context stays flat no matter how many servers you connect (see
[Token scaling](#token-scaling)).

It's a **pure relay**: the orchestrator never interprets the agent's input, it forwards exactly what
the agent sends. The agent does all the thinking. The orchestrator is therefore both:

- an **MCP server** to the agent (it exposes the three meta-tools below), and
- an **MCP client** to each downstream server (it launches them and forwards tool calls).

```
                         ┌─────────────────── orchestrator (this server) ───────────────────┐
   one agent  ──MCP──▶   │  list_capabilities · discover_tools · route                       │
   (the model)           │        │ catalog (config)            │ connection manager (MCP client)
                         └────────┼─────────────────────────────┼─────────────────────────────┘
                                  │                              │
                          orchestrator config            ──MCP──▶  jira MCP      (get_issue, search_issues)
                          (connections + instructions)   ──MCP──▶  codegen MCP   (generate_class)
                                                          ──MCP──▶  files MCP, db MCP, …
```

> **Where this fits.** This is the proven *progressive tool-discovery* pattern (as used by tools
> like [`mcp-cli`](https://www.philschmid.de/mcp-cli) and "dynamic toolsets") — implemented as a
> **.NET / C# MCP server** rather than a shell CLI. If you live in the .NET ecosystem, or want
> self-hostable MCP aggregation you can read and extend in C#, that's the niche it fills. It does
> not include the enterprise layer — auth, multi-tenancy, rate limiting — that gateways like Kong or
> Envoy AI Gateway provide.

---

## Contents

1. [How it works](#how-it-works)
2. [The three tools](#the-three-tools-agent-facing-surface)
3. [Token scaling](#token-scaling)
4. [How it compares](#how-it-compares)
5. [Prerequisites](#prerequisites)
6. [Build & run the demo](#build--run-the-demo)
7. [Register the orchestrator with an agent](#register-the-orchestrator-with-an-agent)
8. [Packaging — install as a .NET tool](#packaging-install-as-a-net-tool)
9. [Add a new downstream MCP](#add-a-new-downstream-mcp)
10. [Configuration reference](#configuration-reference)
11. [Testing](#testing)
12. [Troubleshooting & pitfalls](#troubleshooting--pitfalls)
13. [Security](#security)
14. [Extending](#extending)
15. [Project layout](#project-layout)
16. [Debugging](#debugging)

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
> interpreting is the agent's job: it reads each capability's `summary`/`instructions`, calls
> `discover_tools` to get the real tool schemas, and calls `route` with arguments it fills in. The
> orchestrator never turns a sentence into a tool call — the agent (already an LLM) does that, and
> does it better. This is why the design stays simple and reliable.

Connections are made **lazily** on first use, **cached** per capability for reuse, and
**disposed** on shutdown. A connect that fails or times out is **evicted** so the next call
retries instead of awaiting a dead connection.

---

## The three tools (agent-facing surface)

| Tool | Parameters | Purpose |
| --- | --- | --- |
| `list_capabilities` | — | List the downstream MCPs (name, summary, instructions). Call first. |
| `discover_tools` | `capability` | Connect to one capability and list its tools + input schemas. |
| `route` | `capability`, `tool`, `arguments` | Forward a specific tool call (you pick the tool and fill the arguments) and return its result. |

`route` returns JSON: `capability`, `tool`, `isError`, `text` (flattened text content),
`structured` (when the downstream tool returns structured content), and the `arguments` actually
sent (echoed for auditing). Anything that goes wrong is returned as
`{ "error": ..., "availableCapabilities": [...] }` rather than thrown, so the agent always receives
parseable JSON.

There is deliberately **no "describe it in English" tool**: turning a sentence into a tool call is
interpretation, and that's the agent's job (it already has the schemas from `discover_tools`). The
orchestrator stays a pure relay.

---

## Token scaling

The orchestrator's main benefit is that the agent's **always-loaded tool surface stays constant
at three tools (~600 tokens) no matter how many downstream MCPs you connect.** This is the inverse
of wiring many MCP servers directly into one agent, where *every tool from every server* is
injected as a permanent tool definition (name + description + full JSON schema) and sits in context
on every turn.

| Setup | Always-loaded tool tokens |
| --- | --- |
| **Direct:** 10 MCPs × ~12 tools, ~250 tok per schema | **~30,000 tokens, every turn** |
| **Orchestrator:** the 3 meta-tools | **~600 tokens, every turn — flat** |

Downstream detail moves from *persistent tool definitions* to *on-demand tool results* — content
you only pay for when you fetch it:

| Surface | Cost | When it is in context |
| --- | --- | --- |
| The 3 meta-tools | ~600 tokens, **flat** | always |
| `list_capabilities` | **~100 tokens per capability** | only when called (a transient result) |
| `discover_tools(capability)` | one capability's full schemas | only when called, **one capability at a time** |

So adding the 50th MCP does **not** change the always-on cost. It adds ~100 tokens to a
`list_capabilities` call *when the agent makes one*, and that capability's full schemas load only if
the agent actually inspects it — you never load every capability's schemas at once.

### The flat surface is conditional: route *through* the orchestrator

The three tools are fixed in the orchestrator's code — no agent file can change them. But the agent
file's `tools:` line is a **grant list**, and a user can grant their agent more than three tools by
referencing **other MCP servers directly**, alongside the orchestrator:

```yaml
tools: [ 'orchestrator/*', 'github/*', 'postgres/*' ]
```

The flat ~600-token property holds **only for what is reached through the orchestrator.** Any server
listed directly has *its* tools injected as persistent definitions again — the very cost the
orchestrator removes:

| Agent file `tools:` | Always-loaded tool tokens |
| --- | --- |
| `[ 'orchestrator/*' ]` | ~600, flat — even with 50 capabilities behind it |
| `[ 'orchestrator/*', 'github/*', 'postgres/*' ]` | ~600 **plus** every `github` + `postgres` tool schema |

So put the long tail of tools **behind** the orchestrator (`orchestrator/*`), and only promote a
tool to a direct `tools:` entry when avoiding its discovery round-trip is worth paying its always-on
token cost. Mixing is allowed — it is a deliberate trade, not a free addition.

**The one place cost grows linearly** is the `list_capabilities` listing (~100 tokens ×
capabilities). It stays cheap into the dozens; to keep it small as you scale:

1. **Keep each capability's `summary`/`instructions` terse** — this is the biggest lever and you
   control it in config.
2. **Don't enumerate capabilities in the agent file's prose.** That text *is* persistent
   instruction tokens. Keep the agent file a short guide and let `list_capabilities` be the
   authoritative, on-demand list.
3. **At hundreds of capabilities,** add a category/filter parameter to `list_capabilities` so the
   agent pulls a slice rather than the whole catalog.

The trade-off is **tokens for round-trips**: discovery (`list_capabilities` → `discover_tools` →
`route`) costs a few extra turns, in exchange for a flat ~600-token surface instead of tens of
thousands. For "many MCPs on a modest context budget," that is strongly favorable — which is the
reason this architecture exists.

### Discovered tools are paid once, then discounted (prompt caching)

A reasonable worry: native MCP tool definitions sit in the cached request prefix and get the
"session discount" (prompt caching) every turn, whereas a `discover_tools` payload arrives *mid*
conversation. Does loading tools mid-session lose that discount?

**No — verified empirically.** Prompt caching is prefix-based and conversations are append-only, so
once a `discover_tools` result is in the history it becomes part of the cached prefix: it is a
one-time **cache write** on the turn it appears, then a **cache read** (~10% price) on every later
turn — the same discount native tool definitions get, just starting from the turn it was discovered
and only for the capabilities actually used.

This was confirmed from a live session's own `usage` telemetry (each turn logs
`cache_creation_input_tokens` and `cache_read_input_tokens`). A representative stretch:

```
turn | new_input | cache_WRITE (new delta) | cache_READ (prefix, from cache)
 716 |     2     |        98               |   332,850
 718 |     2     |       610               |   332,948
 720 |     2     |       987               |   333,558
 723 |     2     |     2,255               |   334,545
```

Each turn writes only the **new delta** (the prior reply + new message + any tool results) and
**reads the entire prior prefix from cache** — and that read pool grows as the conversation does. A
discovered-tools block is exactly part of one turn's small `cache_write`, then rides along in
`cache_read` thereafter. So the net effect versus connecting MCPs directly: you avoid caching and
re-reading the *entire* tool catalog every turn, and instead pay a small one-time write per
capability you actually discover. (The same ~5-minute cache TTL applies to both; a gap longer than
that re-writes the prefix regardless of approach.)

---

## How it compares

This implements a well-established pattern — *progressive tool discovery behind a small meta-tool
surface* — so here is an honest map of where it sits, not a claim to have invented it.

| | **This project** | **CLI aggregators** (e.g. [mcp-cli](https://www.philschmid.de/mcp-cli)) | **Enterprise gateways** (e.g. Kong, [Envoy AI Gateway](https://aigateway.envoyproxy.io/docs/0.5/capabilities/mcp/)) |
| --- | --- | --- | --- |
| Integration | **MCP server, typed tools, no shell needed** | CLI binary driven via the agent's shell | MCP server / proxy |
| Tool discovery | progressive (`list` → `discover` → `route`) | progressive | varies |
| Interprets the agent's input | **no — pure relay** | no | varies |
| Enterprise layer (auth, multi-tenancy, rate limiting, observability) | **no** | no | **yes** |
| Runtime | **.NET / C#** | single binary (Bun) | varies (Go, etc.) |

**vs CLI aggregators (mcp-cli):** same core idea, but this is a native MCP server with typed,
schema-validated tools — it needs **no shell/exec**, so it works in sandboxed, hosted, or
non-coding agents where a CLI can't run, and the agent passes structured arguments instead of
hand-built command strings. It also holds **warm cached connections** rather than spawning a fresh
process per call. (mcp-cli is lighter — a single binary — and has a cross-tool search this
deliberately doesn't.)

**vs enterprise gateways (Kong, Envoy, mcp-proxy-server):** those are also MCP servers and add the
production layer — auth, multi-tenancy, rate limiting, observability — that this does **not**. This
is a focused, self-hostable implementation, not an enterprise gateway.

**What's actually distinctive here:** it's **.NET / C# native** (most MCP tooling is TS/Python/Go)
and a deliberately small, self-hostable **pure relay**. The discovery/token-reduction pattern itself
is not novel — the value is the .NET implementation and the no-shell typed-server integration.

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

## Packaging — install as a .NET tool

The orchestrator packs as a **.NET tool** (`dotnet pack`), so it can be installed and referenced by
a command name instead of a project path. It's a single **~1.4 MB**, framework-dependent (needs
.NET 10 installed), portable (one package, all platforms) package.

```bash
# build the package locally
dotnet pack McpOrchestrator/McpOrchestrator.csproj -c Release -o ./nupkg

# install the tool from the local folder (or from a feed once published)
dotnet tool install --global --add-source ./nupkg McpOrchestrator
```

Then register it by command name:

```json
{
  "servers": {
    "orchestrator": { "type": "stdio", "command": "mcp-orchestrator" }
  }
}
```

> **An installed tool ships a minimal *template* catalog**, not the repo's demo config (the demo
> points at sample projects that don't exist once installed). So a fresh install starts with an
> effectively empty catalog plus one disabled example. Point **`MCP_ORCHESTRATOR_CONFIG`** at your
> own config file (recommended, so upgrades don't overwrite it), or edit the shipped
> `orchestrator.config.json` next to the tool. See [Add a new downstream MCP](#add-a-new-downstream-mcp).

### Build everything with one script

[`pack-all.ps1`](../pack-all.ps1) (repo root) produces every artifact into `./dist`, each named with
its runtime id (OS + architecture) and type:

```powershell
./pack-all.ps1                                   # all RIDs: win/linux/osx × x64/arm64
./pack-all.ps1 -Rids win-x64,linux-x64           # just these
./pack-all.ps1 -SkipSelfContained                # only the portable .nupkg tools
./pack-all.ps1 -SelfContainedFormat nupkg        # self-contained as tool packages only (no zips)
```

It emits **three tiers**, so each consumer gets a fitting install option:

| Tier | Example artifact | Approx size | Install | Needs .NET? |
| --- | --- | --- | --- | --- |
| **1. Portable tool** | `McpOrchestrator.0.1.0.nupkg` | 1.4 MB | `dotnet tool install McpOrchestrator` | yes (runtime) |
| **2. Self-contained tool** | `McpOrchestrator.win-x64.0.1.0.nupkg` | ~36 MB | `dotnet tool install McpOrchestrator.win-x64` | CLI only* |
| **3. Self-contained zip** | `McpOrchestrator-0.1.0-win-x64-selfcontained.zip` | ~32 MB | unzip & run | **no** |

**\*** A self-contained *tool* package still needs the **dotnet CLI to install** (`dotnet tool
install` requires it). What it bundles is the *runtime version*, so it runs even if the matching
.NET 10 runtime isn't present. For someone with **no .NET at all**, only the **zip** works — it's
the only artifact that needs nothing installed.

So pick by audience: **tier 1** for developers (smallest), **tier 2** for uniform `dotnet tool`
installs that don't depend on the exact runtime, **tier 3** for zero-install on a machine without
.NET.

---

## Add a new downstream MCP

Adding a capability is a **config-only** change — no code. Edit your config file — the demo
[`orchestrator.config.sample.json`](orchestrator.config.sample.json), or whatever
`MCP_ORCHESTRATOR_CONFIG` points at — and add one entry to `capabilities`.

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
- any other `${NAME}` — resolved from a **process environment variable**; an unresolved placeholder
  is left as-is and logged.

**Config location** is resolved in this order, first hit wins:

1. `MCP_ORCHESTRATOR_CONFIG` environment variable (an explicit file path), then
2. `<solutionDir>/McpOrchestrator/orchestrator.config.json` (if you create one there), then
3. `orchestrator.config.json` next to the built assembly (the shipped template), then
4. `orchestrator.config.json` in the host content root.

> The repo does **not** check in an `orchestrator.config.json` at the default path — only a
> [`orchestrator.config.template.json`](orchestrator.config.template.json) (what installed tools
> ship) and an [`orchestrator.config.sample.json`](orchestrator.config.sample.json) demo catalog
> that the SmokeTest and IDE configs point `MCP_ORCHESTRATOR_CONFIG` at explicitly.

A missing or invalid config is non-fatal: the server starts with **zero capabilities** and logs
a warning/error rather than crashing.

### Setting environment variables (`MCP_ORCHESTRATOR_CONFIG` and the rest)

All of the orchestrator's environment variables are read from the process the host launches, so the
simplest place to set them is the **`env` block of the server entry** in your host config — scoped
to the orchestrator, explicit, and no machine-wide changes:

```json
{
  "servers": {
    "orchestrator": {
      "type": "stdio",
      "command": "mcp-orchestrator",
      "env": { "MCP_ORCHESTRATOR_CONFIG": "C:/Users/you/my-orchestrator.config.json" }
    }
  }
}
```

The same block is where you'd set `MCP_ORCHESTRATOR_DEBUG`. Alternatively set them as OS environment
variables — **user-level**
(e.g. Windows `setx`, or a shell rc file) or **machine-level** (needs admin) — but then **restart
the MCP host**, since a child process captures its environment at launch.

---

## Testing

```bash
dotnet test McpOrchestrator.slnx
```

The `McpOrchestrator.Tests` project (xUnit) covers:

- **Unit** — catalog validation and dedup, `${VAR}` substitution, invalid-JSON resilience, and
  argument parsing (object / JSON-string / scalar / array / null / omitted).
- **Integration** — against the real demo server as a live downstream process: connect + list,
  call a tool, unknown capability, downstream failure surfaced as `isError`, **call timeout**,
  **connect timeout**, bad-command eviction, and 20-way concurrency on one cached connection.
- **End-to-end** — the tool methods driven through the full catalog → connection manager →
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

- **More transports** — `DownstreamConnectionManager` implements only `stdio`; add HTTP/SSE
  (the SDK ships an HTTP client transport) by branching on `descriptor.Transport`.
- **Connection lifecycle** — lazy connect, per-capability cache, fault eviction, and timeouts are
  already in place; new transports should reuse the same `GetClientAsync` path.

---

## Project layout

```
McpOrchestrator/                         The orchestrator tool package
  Program.cs                              Entry point: OrchestratorHost.RunAsync(args)
  OrchestratorHost.cs                     Host wiring (DI, MCP server)
  orchestrator.config.template.json      Minimal template shipped with the installed tool
  orchestrator.config.sample.json        Demo catalog (jira/codegen/files) the SmokeTest/IDE point at
  Tools/OrchestratorTool.cs              The 3 meta-tools: list_capabilities/discover_tools/route
  Orchestration/
    CapabilityDescriptor.cs              Config POCO: one downstream MCP (+ OrchestratorConfig root)
    ICapabilityCatalog.cs                The address book of downstream capabilities
    CapabilityCatalog.cs                 Loads + validates the catalog from JSON; resolves ${VAR} placeholders
    IDownstreamConnectionManager.cs      Contract: list/call downstream tools (+ CapabilityNotFoundException)
    DownstreamConnectionManager.cs       MCP client: lazy connect, cache, timeouts, proxy, dispose
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
