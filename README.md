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

👉 **Full documentation:** [`McpOrchestrator/README.md`](McpOrchestrator/README.md) — how it
works, setup, adding new MCPs, configuration reference, testing, and troubleshooting.

## Quick start

Get the binary, point your agent at it, and list the downstream servers it should relay to. **Two
config files** are involved:

- **Host config** — your agent's existing MCP file: `.mcp.json` (Claude Code and Visual Studio) or
  `.vscode/mcp.json` (VS Code). You add the orchestrator as a server here.
- **Orchestrator config** — a new file you create (e.g. `orchestrator.config.json`), pointed to by
  the `MCP_ORCHESTRATOR_CONFIG` environment variable. It lists the downstream MCP servers.

### 1. Get the orchestrator binary

Pick one:

```bash
# A. As a .NET tool (needs the .NET runtime) — gives you the command `mcp-orchestrator`:
dotnet tool install --global McpOrchestrator
```

**B. As a self-contained Native-AOT binary** — a single executable, no .NET runtime required.
Download it from the [GitHub Releases](https://github.com/Byggarepop/dotnet-mcp-orchestrator/releases)
(`McpOrchestrator-<version>-<rid>.zip`) and unzip, or
[build it yourself](McpOrchestrator/README.md#native-aot-smallest-self-contained-binary-fastest-startup).
You then use the absolute path to the binary as the command.

### 2. Add the orchestrator to your host config (`.mcp.json` / `.vscode/mcp.json`)

The agent only ever sees *this one* server:

```jsonc
{
  "servers": {
    "dotnet-mcp-orchestrator": {
      "type": "stdio",
      // the installed tool command, or an absolute path to the AOT binary:
      "command": "mcp-orchestrator",
      "args": [],
      "env": {
        // absolute path to the orchestrator config you create in step 3:
        "MCP_ORCHESTRATOR_CONFIG": "<ABSOLUTE-PATH-TO>/orchestrator.config.json"
      }
    }
  }
}
```

### 3. List your downstream servers in the orchestrator config (`orchestrator.config.json`)

This is the file you pointed `MCP_ORCHESTRATOR_CONFIG` at. Each entry is one capability the agent can
route to; `command`/`args`/`env` are how that downstream MCP is launched (`${SOLUTION_DIR}`,
`${CONFIG_DIR}`, and any `${ENV_VAR}` are substituted):

```jsonc
{
  "capabilities": [
    {
      "name": "files",
      "summary": "Read and write files on the local machine.",
      "instructions": "",
      "enabled": true,
      "transport": "stdio",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-filesystem", "<ABSOLUTE-PATH-TO>/projects"],
      "connectTimeoutSeconds": 30
    },
    {
      "name": "Tokensaver",
      "summary": "Reduce tokens spent when working with .NET (outline/minify/trace DI).",
      "instructions": "",
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

### 4. Restart the MCP host

The agent now sees the three meta-tools and the flow is
`list_capabilities` → `discover_tools("Tokensaver")` → `route("Tokensaver", "outline_c_sharp_file", { … })`.

> **Notes.** `instructions` is optional (a usage hint surfaced to the agent — leave it `""`).
> `env`/`workingDirectory` are per-capability and optional. The config file supports `//` comments.
> Logs are mirrored to `~/.dotnet-orchestrator-mcp/orchestrator.log`. See the
> [full documentation](McpOrchestrator/README.md) for every field, packaging, and troubleshooting.

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
agent does the thinking. See the [full docs](McpOrchestrator/README.md) for details.
