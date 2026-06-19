# MCP Orchestrator

An **MCP server that orchestrates other MCP servers.** One agent connects to this single
server; it holds connections to many downstream MCP servers (JIRA, code generation, a
filesystem server, …) and routes the agent's requests to the right one — so you use **one
agent + one MCP** instead of switching agents per toolset.

👉 **Full documentation:** [`McpOrchestrator/README.md`](McpOrchestrator/README.md) — how it
works, setup, adding new MCPs, configuration reference, testing, and troubleshooting.

## Quick start

```bash
dotnet build McpOrchestrator.slnx                              # build everything
dotnet run --project McpOrchestrator.SmokeTest --no-build      # run the end-to-end demo
dotnet test McpOrchestrator.slnx                               # run the test suite
```

## Projects

| Project | Role |
| --- | --- |
| `McpOrchestrator` | The orchestrator: MCP server to the agent, MCP client to downstream servers. |
| `McpOrchestrator.DemoMcp` | Sample downstream MCP (`--persona jira` / `codegen` / `diag`). |
| `McpOrchestrator.SmokeTest` | Console MCP client that drives the orchestrator end-to-end. |
| `McpOrchestrator.Tests` | xUnit suite: unit + integration + end-to-end. |

## The four tools

`list_capabilities` → `discover_tools` → **`route`** (preferred: you pick the tool and fill the
arguments) — with `request` as a best-effort natural-language fallback. The orchestrator is a
**courier, not an interpreter**: it forwards exactly what the agent sends, so each capability's
config `instructions` spell out precisely what to pass. See the
[full docs](McpOrchestrator/README.md) for details.
