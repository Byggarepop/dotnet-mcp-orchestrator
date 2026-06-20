# McpOrchestrator — a .NET-native MCP orchestrator

**Route one agent through one server to many MCP servers, with progressive tool discovery to keep
context small — plus an optional fully-local, no-API natural-language router.**

One agent connects to this single server; it holds connections to many downstream MCP servers
(JIRA, code generation, a filesystem server, …) and routes the agent's requests to the right one —
so you use **one agent + one MCP** instead of switching agents per toolset. Downstream tools are
discovered on demand, so the agent's always-loaded context stays flat no matter how many servers
you connect.

> **Where this fits:** the proven progressive tool-discovery pattern (like
> [`mcp-cli`](https://www.philschmid.de/mcp-cli) / "dynamic toolsets"), implemented as a **.NET / C#
> MCP server** rather than a shell CLI, with a **local-first twist** (optional in-process model for
> natural-language routing, no cloud API). The niche: .NET shops and anyone wanting self-hosted /
> offline MCP aggregation they can read and extend in C#. It does not include the enterprise layer
> (auth, multi-tenancy, rate limiting) that gateways like Kong or Envoy provide.

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
| `McpOrchestrator` | The orchestrator: MCP server to the agent, MCP client to downstream servers. Lean core tool (~1.4 MB). |
| `McpOrchestrator.LocalLlm` | Optional fat tool: the same host + the embedded local LLM (~50 MB). |
| `McpOrchestrator.DemoMcp` | Sample downstream MCP (`--persona jira` / `codegen` / `diag`). |
| `McpOrchestrator.SmokeTest` | Console MCP client that drives the orchestrator end-to-end. |
| `McpOrchestrator.Tests` | xUnit suite: unit + integration + end-to-end. |

## The four tools

`list_capabilities` → `discover_tools` → **`route`** (preferred: you pick the tool and fill the
arguments) — with `request` as a best-effort natural-language fallback. The orchestrator is a
**courier, not an interpreter**: it forwards exactly what the agent sends, so each capability's
config `instructions` spell out precisely what to pass.

An **optional embedded local LLM** (opt-in via `MCP_ORCHESTRATOR_PLANNER=llm`) makes `request`
reliable — a tiny model runs in-process on CPU with grammar-constrained decoding, downloaded once
on first use. See the [full docs](McpOrchestrator/README.md) for details.
