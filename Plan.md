# Plan: ConsafeWorkflow.Mcp — MCP Orchestrator

## Goal

One agent, many MCP servers. Instead of switching agents to switch toolsets, a single agent
talks to **one** MCP server — the **orchestrator** — which holds connections and instructions
to downstream MCP servers (JIRA, code generation, DB search, …) and routes the agent's
requests to the right one.

The orchestrator is both an **MCP server** (to the agent) and an **MCP client** (to the
downstream servers).

## Flow

1. Agent calls `list_capabilities` → orchestrator returns the config-driven catalog (name +
   summary + instructions per downstream MCP).
2. Agent expresses what it needs:
   - `route(capability, tool, arguments)` — a specific downstream tool, or
   - `request(capability, request)` — plain language; the orchestrator picks the tool.
   (`discover_tools(capability)` lists a capability's tools + schemas in between.)
3. Orchestrator connects to that downstream MCP, invokes the tool, relays the result back.

## Status (2026-06-19) — PROTOTYPE WORKING

Pivoted from the previous "stateful workflow engine" design (preserved on the
`archive/workflow-shell` git branch). The orchestrator prototype is built and verified
end-to-end by `ConsafeWorkflow.SmokeTest`:

- `list_capabilities` advertises `jira` + `codegen` from `orchestrator.config.json`.
- `discover_tools` connects to a downstream MCP and relays its tool schemas.
- `route` forwards a specific tool call (e.g. `jira/get_issue {issueKey:PROJ-1}`) and returns
  the result.
- `request` routes a natural-language need (e.g. "status of PROJ-3") via `HeuristicRoutePlanner`
  and returns the result + a rationale.
- Routing across **two distinct** downstream servers (jira, codegen) works through the one
  agent-facing surface; unknown capabilities return a structured error, not a crash.

## Architecture

- **Catalog** (`ICapabilityCatalog` / `CapabilityCatalog`) — loads downstream definitions from
  `orchestrator.config.json`; resolves `${SOLUTION_DIR}`/`${CONFIG_DIR}`/env tokens.
- **Connection manager** (`IDownstreamConnectionManager` / `DownstreamConnectionManager`) — the
  MCP client: lazy connect per capability over stdio, cache, proxy `ListTools`/`CallTool`,
  dispose on shutdown, evict on failed connect.
- **Route planner** (`IRoutePlanner` / `HeuristicRoutePlanner`) — turns a NL request into a
  concrete tool call. The seam where a local/cloud LLM plugs in; the shipped planner is a
  dependency-free heuristic.
- **Tools** (`OrchestratorTool`) — the four meta-tools the agent sees.

## Tech

- .NET 10 (`net10.0`), SDK 10.0.300.
- `ModelContextProtocol` 1.4.0 (server + hosting) and `ModelContextProtocol.Core` 1.4.0
  (client: `McpClient`, `StdioClientTransport`).
- stdio transport both inbound (agent → orchestrator) and outbound (orchestrator → downstream).

## Projects

- `ConsafeWorkflow.Mcp` — the orchestrator.
- `ConsafeWorkflow.DemoMcp` — sample downstream MCP; `--persona jira|codegen` selects toolset.
- `ConsafeWorkflow.SmokeTest` — console MCP client that drives the orchestrator end-to-end.

## Next steps

- LLM-backed `IRoutePlanner` (local Ollama/LM Studio) for real `request` routing.
- Additional transports (HTTP/SSE) in the connection manager.
- Optionally re-expose downstream tools directly (namespaced passthrough) in addition to the
  `route`/`request` meta-tools, for agents that prefer calling tools by name.
- Health/preflight for downstream servers; per-capability auth/secrets handling.

## Notes

- Logging goes to stderr; stdout is reserved for the MCP stdio protocol.
- `--no-build` in the MCP registration requires a prior `dotnet build`.
