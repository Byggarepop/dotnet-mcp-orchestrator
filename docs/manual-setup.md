# Manual setup

[`init`](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#register-the-orchestrator-with-an-agent)
automates this; do it by hand if you have no host config yet or want full control. **Two config
files** are involved:

- **Host config** — your agent's existing MCP file: `.mcp.json` (Claude Code and Visual Studio) or
  `.vscode/mcp.json` (VS Code). You add the orchestrator as a server here.
- **Orchestrator config** — a new file you create (e.g. `orchestrator.config.json`), pointed to by
  the `MCP_ORCHESTRATOR_CONFIG` environment variable. It lists the downstream MCP servers.

## 1. Add the orchestrator to your host config (`.mcp.json` / `.vscode/mcp.json`)

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

## 2. List your downstream servers in the orchestrator config (`orchestrator.config.json`)

This is the file you pointed `MCP_ORCHESTRATOR_CONFIG` at. Each entry is one capability the agent
can route to; `command`/`args`/`env` are how that downstream MCP is launched. Use plain absolute
paths here — you don't need any special syntax. Optionally, `${...}` placeholders are substituted
if you want them: `${CONFIG_DIR}` (the folder this config lives in) and any `${ENV_VAR}` (a
process environment variable, e.g. for API keys):

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

The agent now sees the three meta-tools and the flow is `list_capabilities` →
`discover_tools("Tokensaver")` → `route("Tokensaver", "outline_c_sharp_file", { … })`.

## Notes

`summary` is what the agent routes on. `instructions` is an optional usage hint surfaced to the
agent — omit it unless a capability needs one. For a capability the agent should call *unprompted*
(e.g. a check to run after every edit), set `"promote": true` — its `instructions` are then
hoisted into the MCP initialize handshake so the trigger text is in the agent's context from turn
one; see [Proactive capabilities](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#proactive-capabilities-promote).
`env`/`workingDirectory` are per-capability and optional. The config file supports `//` comments.
There's also a `${SOLUTION_DIR}` placeholder, but you almost certainly don't need it — it resolves
to this repo's solution root and exists only so the in-repo sample configs can find sibling demo
servers. For your own setup, use absolute paths, `${CONFIG_DIR}`, or `${ENV_VAR}` instead. Logs
are mirrored to `~/.mcpOrchestrator/orchestrator.log`. See the
[full documentation](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md)
for every field, packaging, and troubleshooting.

**Testing a local build?** `pack-local.ps1` packs the project as a pinned `9.9.9-dev` version into
`nupkg/local-feed`; then `init --dev-feed nupkg/local-feed` wires the host to launch the
orchestrator from that feed, so it always runs your latest local code. Re-run `pack-local.ps1` and
restart the host to pick up changes.
