# McpOrchestrator — a .NET-native MCP orchestrator

**Route one agent through one server to many MCP servers, with progressive tool discovery to keep
the agent's context small.**

One agent connects to this single server; it holds connections to many downstream MCP servers
(JIRA, code generation, a filesystem server, …) and relays the agent's calls to the right one —
so you use **one agent + one MCP** instead of switching agents per toolset. Downstream tools are
discovered on demand, so the agent's always-loaded context stays flat no matter how many servers
you connect. It's a **pure relay** — the orchestrator never interprets the agent's input.

> **Where this fits:** the proven progressive tool-discovery pattern (like
> [`mcp-cli`](https://www.philschmid.de/mcp-cli) / "dynamic toolsets"), implemented as a **.NET / C#
> MCP server** rather than a shell CLI. The niche: .NET shops and anyone wanting self-hostable MCP
> aggregation they can read and extend in C#. It does not include the enterprise layer (auth,
> multi-tenancy, rate limiting) that gateways like Kong or Envoy provide.

👉 **Full documentation:** [`McpOrchestrator/README.md`](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) — how it
works, setup, adding new MCPs, configuration reference, testing, and troubleshooting.

## Quick start

Three steps from an existing MCP setup to running through the orchestrator.

### 1. Install

The .NET tool (needs the .NET runtime) puts the `mcp-orchestrator` command on your PATH:

```bash
dotnet tool install --global McpOrchestrator
```

> No .NET runtime? Download the self-contained Native-AOT binary from
> [GitHub Releases](https://github.com/Byggarepop/dotnet-mcp-orchestrator/releases), unzip it, and use
> the absolute path to the binary as the command (pass it to `init` below with `--command <path>`).

### 2. Init

Point `init` at your existing MCP host config — `.mcp.json` (Claude Code, Visual Studio),
`.vscode/mcp.json` (VS Code), or a Cursor / Claude Desktop config:

```bash
mcp-orchestrator init path/to/.mcp.json
```

It lifts every stdio server into a generated `orchestrator.config.json` (one capability each), backs
up the original to `.bak`, then rewrites it to launch **only** the orchestrator — pointed at the new
catalog via `MCP_ORCHESTRATOR_CONFIG`. Remote (http/sse) servers can't be relayed over stdio, so
they're left in place untouched. (Add `--dry-run` to preview both files first.)

### 3. Add summary text

Open the generated `orchestrator.config.json` and replace each capability's `TODO` `summary` with a
one-line description of when the agent should use it — that line is what the agent reads to route.
Then restart your MCP host.

That's it. The agent now sees three meta-tools and the flow is
`list_capabilities` → `discover_tools("…")` → `route("…", "<tool>", { … })`.

> Starting from scratch with no MCP config yet? See [Manual setup](#manual-setup) for the two files
> `init` would otherwise generate. Logs are mirrored to `~/.mcpOrchestrator/orchestrator.log`.

## Manual setup

`init` automates this; do it by hand if you have no host config yet or want full control. **Two
config files** are involved:

- **Host config** — your agent's existing MCP file: `.mcp.json` (Claude Code and Visual Studio) or
  `.vscode/mcp.json` (VS Code). You add the orchestrator as a server here.
- **Orchestrator config** — a new file you create (e.g. `orchestrator.config.json`), pointed to by
  the `MCP_ORCHESTRATOR_CONFIG` environment variable. It lists the downstream MCP servers.

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

This is the file you pointed `MCP_ORCHESTRATOR_CONFIG` at. Each entry is one capability the agent can
route to; `command`/`args`/`env` are how that downstream MCP is launched (`${SOLUTION_DIR}`,
`${CONFIG_DIR}`, and any `${ENV_VAR}` are substituted):

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
      "workingDirectory": "${SOLUTION_DIR}",
      "env": {
        "TOKENSAVER_API_URL": "https://tokensavermcp.com",
        "TOKENSAVER_UPDATE_INTERVAL_MINUTES": "0"
      }
    }
  ]
}
```

Restart the MCP host to pick it up.

> **Notes.** `summary` is what the agent routes on. `instructions` is an optional usage hint — omit
> it unless a capability needs one. `env`/`workingDirectory` are per-capability and optional. The
> config file supports `//` comments. See the
> [full documentation](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) for every field, packaging, and troubleshooting.

> **Testing a local build?** `pack-local.ps1` packs the project as a pinned `9.9.9-dev` version into
> `nupkg/local-feed`; then `init --dev-feed nupkg/local-feed` wires the host to launch the
> orchestrator from that feed, so it always runs your latest local code. Re-run `pack-local.ps1` and
> restart the host to pick up changes.

## Projects

| Project | Role |
| --- | --- |
| `McpOrchestrator` | The orchestrator: MCP server to the agent, MCP client to downstream servers. The ~1.4 MB tool. |
| `McpOrchestrator.DemoMcp` | Sample downstream MCP (`--persona jira` / `codegen` / `diag`). |
| `McpOrchestrator.SmokeTest` | Console MCP client that drives the orchestrator end-to-end. |
| `McpOrchestrator.Tests` | xUnit suite: unit + integration + end-to-end. |

## The three tools

`list_capabilities` → `discover_tools` → **`route`** (you pick the tool and fill the arguments). The
orchestrator is a **courier, not an interpreter**: it forwards exactly what the agent sends — the
agent does the thinking. See the [full docs](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md) for details.
