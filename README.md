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

### 1. See it work (no setup)

```bash
dotnet build McpOrchestrator.slnx                              # build everything
dotnet run --project McpOrchestrator.SmokeTest --no-build      # run the end-to-end demo
dotnet test McpOrchestrator.slnx                               # run the test suite
```

The demo drives the orchestrator end-to-end against a sample downstream server (jira / codegen /
files) — no config or agent needed.

### 2. Wire it to your agent (full example)

Two files do everything: your **host config** points the agent at the orchestrator, and the
orchestrator's **own config** lists the downstream MCP servers it relays to.

**a. Get the orchestrator binary.** Either install the tool (`dotnet tool install --global
McpOrchestrator` → command `mcp-orchestrator`) or build the self-contained
[Native-AOT binary](McpOrchestrator/README.md#native-aot-smallest-self-contained-binary-fastest-startup)
(a single .exe, no runtime needed).

**b. Register the orchestrator with your agent** — e.g. Claude Code's `.mcp.json` (VS Code uses the
same shape under `servers`). The agent only ever sees *this one* server:

```jsonc
{
  "servers": {
    "dotnet-mcp-orchestrator": {
      "type": "stdio",
      // the installed tool command, or an absolute path to the AOT binary:
      "command": "mcp-orchestrator",
      "args": [],
      "env": {
        // where the orchestrator finds its catalog (absolute path):
        "MCP_ORCHESTRATOR_CONFIG": "<ABSOLUTE-PATH-TO>/orchestrator.config.json"
      }
    }
  }
}
```

**c. Tell the orchestrator which downstream servers to relay to** — the file you pointed
`MCP_ORCHESTRATOR_CONFIG` at. Each entry is one capability the agent can route to; `command`/`args`/
`env` are how that downstream MCP is launched (`${SOLUTION_DIR}`, `${CONFIG_DIR}`, and `${ENV_VARS}`
are substituted):

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

**d. Restart the MCP host.** The agent now sees the three meta-tools and the flow is
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
