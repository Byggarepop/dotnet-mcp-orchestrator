# Development

Working on the orchestrator itself — building, testing, extending, and debugging. Users of the
published tool don't need anything on this page.

## Build & run the demo

This repo has four projects:

- **`McpOrchestrator`** — the orchestrator itself.
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
    Skills/                              Agent Skills: sources (directory/git/http), governance,
                                         hot reload, SEP-2640 skill:// resources (see Sep2640Conventions.cs)

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
