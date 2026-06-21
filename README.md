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

```bash
dotnet build McpOrchestrator.slnx                              # build everything
dotnet run --project McpOrchestrator.SmokeTest --no-build      # run the end-to-end demo
dotnet test McpOrchestrator.slnx                               # run the test suite
```

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
