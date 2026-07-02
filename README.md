<!-- mcp-name: io.github.Byggarepop/dotnet-mcp-orchestrator -->

# McpOrchestrator — a .NET-native MCP orchestrator

[![NuGet](https://img.shields.io/nuget/v/McpOrchestrator.svg)](https://www.nuget.org/packages/McpOrchestrator)
[![Downloads](https://img.shields.io/nuget/dt/McpOrchestrator.svg)](https://www.nuget.org/packages/McpOrchestrator)
[![License: MIT](https://img.shields.io/github/license/Byggarepop/dotnet-mcp-orchestrator.svg)](LICENSE)

**Every MCP server you connect costs context before the agent does anything — its tool manifests sit in the prompt on every turn. Connect enough of them and you're spending tens of thousands of tokens just describing tools the agent isn't using yet.**

McpOrchestrator puts one server between your agent and all the others. It loads downstream tool manifests **on demand** instead of upfront, so the agent's always-on context stays flat no matter how many servers you add. It's a **pure relay** — the orchestrator never interprets the agent's input.

## Measured impact

Running against a real workplace MCP setup, measured with the Copilot CLI's `/usage`:

| | Tokens in context |
| --- | --- |
| MCP connected directly (manifests loaded upfront) | **17,900** |
| Same MCP behind McpOrchestrator | **1,400** |
| **Reduction** | **~13x** |

The savings scale with the number of servers: the more MCPs you connect, the more upfront manifest cost you avoid. Measure your own setup with the `profile` command below — no install, nothing changed.

## How it works

One agent connects to this single server; it holds connections to many downstream MCP servers (JIRA, code generation, a filesystem server, …) and relays the agent's calls to the right one — so you use **one agent + one MCP** instead of switching agents per toolset. Downstream tools are discovered on demand via three meta-tools:

`list_capabilities` → `discover_tools("Tokensaver")` → `route("Tokensaver", "outline_c_sharp_file", { … })`

## Where this fits

The value here is the **smallest possible surface**: a pure stdio relay that installs as a single .NET tool (or AOT binary, no runtime), with nothing to read but the routing path. Progressive discovery isn't unique to it — the closest .NET alternative, [mcp-aggregator](https://github.com/MarimerLLC/mcp-aggregator), does more (REST API alongside MCP, runtime server registration, credential management), so reach for that if you want a gateway. Reach for this if you want the leanest possible relay with nothing extra to run or reason about. It deliberately omits the enterprise layer — auth, multi-tenancy, rate limiting — that gateways like Kong or Envoy provide.

👉 **Full documentation:** [`McpOrchestrator/README.md`](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) — how it works, setup, adding new MCPs, configuration reference, testing, and troubleshooting.

## Is this tool for you? Check in one command — no install

Before changing anything, see what the orchestrator would actually save for *your* servers. `cd` into a folder that already holds an MCP config and run `profile` with **no path at all** — it auto-detects the config, connects to each stdio server once, then prints the token savings — **nothing is installed and not a single file is changed** (needs the .NET SDK):

```bash
cd ~/my-project          # a folder with a .mcp.json / .vscode/mcp.json / Cursor config
dotnet tool execute McpOrchestrator profile
```

It picks the first config it finds (`orchestrator.config.json`, `.mcp.json`, `.vscode/mcp.json`, `.cursor/mcp.json`, `mcp.json`). There's also the option to point at a config explicitly instead — handy when it lives elsewhere or you want to be sure which one is read:

```bash
dotnet tool execute McpOrchestrator profile --host-config <your .mcp.json / Cursor / Claude Desktop config>
```

Remote (`http`/`sse`) servers can't be relayed and are skipped; everything else is measured in memory. If the numbers look good, set it up below — if not, you're done, with no cleanup. (Full details and a local-build variant: the [profiling guide](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#profiling-token-economics-profile).)

## Quick start

Three steps from an existing MCP setup to running through the orchestrator.

### 1. Install (or don't)

The .NET tool (needs the **.NET SDK** — `dotnet tool` ships with the SDK, not the runtime) puts the `mcp-orchestrator` command on your PATH:

```bash
dotnet tool install --global McpOrchestrator
```

One-shot commands (`init`, `profile`) also run install-free straight from nuget.org — nothing to install, nothing to go stale:

```bash
dotnet tool execute McpOrchestrator --yes init     # dnx McpOrchestrator --yes init  works too
```

For the long-running server entry itself, the installed `mcp-orchestrator` command (or the AOT binary) is what `init` wires into your host config by default.

> No .NET SDK? Download the self-contained Native-AOT binary from [GitHub Releases](https://github.com/Byggarepop/dotnet-mcp-orchestrator/releases), unzip it, and use the absolute path to the binary as the command (pass it to `init` below with `--command <path>`). It needs no .NET at all.

> **Running a local dev build?** Never `tool install` the `9.9.9-dev` package — its version never changes, so the installed copy silently goes stale after the next `pack-local.ps1`. Use the execute-from-feed entry that `init --dev-feed` writes (see the note at the end of this section).

### 2. Init

`cd` into a folder that holds your MCP host config and run `init` with no path — it auto-detects the first of `.mcp.json`, `.vscode/mcp.json`, `.cursor/mcp.json`, `mcp.json`:

```bash
cd ~/my-project
mcp-orchestrator init
```

Or point it at a specific config — `.mcp.json` (Claude Code, Visual Studio), `.vscode/mcp.json` (VS Code), or a Cursor / Claude Desktop config:

```bash
mcp-orchestrator init path/to/.mcp.json
```

It lifts every stdio server into a generated `orchestrator.config.json` (one capability each), backs up the original to `.bak`, then rewrites it to launch **only** the orchestrator — pointed at the new catalog via `MCP_ORCHESTRATOR_CONFIG`. Along the way it connects to each server once and auto-generates its one-line `summary` from what the server declares about itself (its `initialize` instructions, else its tool names) — no LLM, fully offline. Remote (http/sse) servers can't be relayed over stdio, so they're left in place untouched. (Add `--dry-run` to preview both files first; add `--no-summarize` to skip the server connections and keep `TODO` placeholders instead.)

### 3. Review the summaries (optional)

Open the generated `orchestrator.config.json` and review the auto-generated summaries — each is marked with a trailing `// auto-generated` comment — and refine any that look off; that line is what the agent reads to route. A server that failed to start keeps a `TODO` placeholder to fill in by hand. Then restart your MCP host so it picks up the orchestrator entry.

That's it. The agent now sees three meta-tools and the flow is `list_capabilities` → `discover_tools("…")` → `route("…", "<tool>", { … })`. And from here on you can edit `orchestrator.config.json` at any time — the running orchestrator [hot-reloads it](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#hot-reload), no restart needed.

> Team setup? Serve one shared catalog from an HTTPS URL via `MCP_ORCHESTRATOR_CONFIG_URL` — every developer picks up changes automatically within the poll interval. See [Central configuration](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#central-configuration); bootstrap the shared file with `init --print-central`.

> Starting from scratch with no MCP config yet? See [Manual setup](#manual-setup) for the two files `init` would otherwise generate. Logs are mirrored to `~/.mcpOrchestrator/orchestrator.log`.

## Manual setup

`init` automates this; do it by hand if you have no host config yet or want full control. **Two config files** are involved:

- **Host config** — your agent's existing MCP file: `.mcp.json` (Claude Code and Visual Studio) or `.vscode/mcp.json` (VS Code). You add the orchestrator as a server here.
- **Orchestrator config** — a new file you create (e.g. `orchestrator.config.json`), pointed to by the `MCP_ORCHESTRATOR_CONFIG` environment variable. It lists the downstream MCP servers.

### 1. Add the orchestrator to your host config (`.mcp.json` / `.vscode/mcp.json`)

The agent only ever sees *this one* server:

```jsonc
{
  "servers": {
    "orchestrator": {
      "type": "stdio",
      // The command the tool put on your PATH — `mcp-orchestrator`, NOT the package
      // id `McpOrchestrator`. (Or the absolute path to the AOT binary instead.)
      "command": "mcp-orchestrator",
      "args": [],
      "env": {
        // absolute path to the orchestrator config you create in step 2:
        "MCP_ORCHESTRATOR_CONFIG": "<ABSOLUTE-PATH-TO>/orchestrator.config.json"
      }
    }
  }
}
```

### 2. List your downstream servers in the orchestrator config (`orchestrator.config.json`)

This is the file you pointed `MCP_ORCHESTRATOR_CONFIG` at. Each entry is one capability the agent can route to; `command`/`args`/`env` are how that downstream MCP is launched. Use plain absolute paths here — you don't need any special syntax. Optionally, `${...}` placeholders are substituted if you want them: `${CONFIG_DIR}` (the folder this config lives in) and any `${ENV_VAR}` (a process environment variable, e.g. for API keys):

```jsonc
{
  "capabilities": [
    {
      "name": "files",
      "summary": "Read and write files on the local machine.",
      "enabled": true,
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "<ABSOLUTE-PATH-TO>/projects"],
      "connectTimeoutSeconds": 30
    },
    {
      "name": "Tokensaver",
      "summary": "Reduce tokens spent when working with .NET (outline/minify/trace DI).",
      "enabled": true,
      "transport": "stdio",
      "command": "dotnet",
      "args": ["tool", "execute", "TokenSaver.Mcp", "--yes"],
      "env": {
        "TOKENSAVER_API_URL": "https://tokensavermcp.com",
        "TOKENSAVER_UPDATE_INTERVAL_MINUTES": "0"
      }
    }
  ]
}
```

Restart the MCP host to pick it up.

The agent now sees the three meta-tools and the flow is `list_capabilities` → `discover_tools("Tokensaver")` → `route("Tokensaver", "outline_c_sharp_file", { … })`.

> **Notes.** `summary` is what the agent routes on. `instructions` is an optional usage hint surfaced to the agent — omit it unless a capability needs one. `env`/`workingDirectory` are per-capability and optional. The config file supports `//` comments. There's also a `${SOLUTION_DIR}` placeholder, but you almost certainly don't need it — it resolves to this repo's solution root and exists only so the in-repo sample configs can find sibling demo servers. For your own setup, use absolute paths, `${CONFIG_DIR}`, or `${ENV_VAR}` instead. Logs are mirrored to `~/.mcpOrchestrator/orchestrator.log`. See the [full documentation](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) for every field, packaging, and troubleshooting.

> **Testing a local build?** `pack-local.ps1` packs the project as a pinned `9.9.9-dev` version into `nupkg/local-feed`; then `init --dev-feed nupkg/local-feed` wires the host to launch the orchestrator from that feed, so it always runs your latest local code. Re-run `pack-local.ps1` and restart the host to pick up changes.

## Projects

| Project | Role |
| --- | --- |
| `McpOrchestrator` | The orchestrator: MCP server to the agent, MCP client to downstream servers. The ~1.4 MB tool. |
| `McpOrchestrator.DemoMcp` | Sample downstream MCP (`--persona jira` / `codegen` / `diag`). |
| `McpOrchestrator.SmokeTest` | Console MCP client that drives the orchestrator end-to-end. |
| `McpOrchestrator.Tests` | xUnit suite: unit + integration + end-to-end. |

## The three tools

`list_capabilities` → `discover_tools` → **`route`** (you pick the tool and fill the arguments). The orchestrator is a **courier, not an interpreter**: it forwards exactly what the agent sends — the agent does the thinking. See the [full docs](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) for details.
