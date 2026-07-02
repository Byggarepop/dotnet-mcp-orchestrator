# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow reads the section matching the tag (e.g. `## [0.1.0]` for tag `v0.1.0`) and
uses it as the GitHub Release notes — so keep an entry per released version.

## [Unreleased]

### Changed
- `init` now writes an install-free host entry by default: the orchestrator is launched via
  `dotnet tool execute McpOrchestrator --version <the version init ran as> --yes` (resolved from
  the local NuGet cache — no global install needed, nothing to go stale). `--command` still
  overrides it (e.g. a globally installed `mcp-orchestrator` or the AOT binary path), and
  `--dev-feed` is unchanged. The quick start is now two steps with no install step.

### Added
- Centrally managed config: set `MCP_ORCHESTRATOR_CONFIG_URL` to serve the catalog from an HTTPS
  URL (team scenario — one shared catalog, updated in one place, picked up automatically). Source
  selection is binary: the URL wins and the local `MCP_ORCHESTRATOR_CONFIG` path is ignored with a
  warning; configs are never merged. Polling (default 300 s, `MCP_ORCHESTRATOR_CONFIG_POLL_SECONDS`,
  ±10% jitter) uses ETag/If-None-Match so unchanged configs cost a 304 and skip the reload
  pipeline; failures keep the running config, log actionable errors (distinct for 401/403), and
  back off exponentially up to 15 minutes. Optional `MCP_ORCHESTRATOR_CONFIG_AUTH` is sent
  verbatim as the Authorization header and never logged. Successful fetches are cached atomically
  under `~/.mcpOrchestrator/` for offline startup (same-URL cache only; no cache → startup fails
  loudly rather than falling back to a local file). Central payloads reject the machine-local
  `${CONFIG_DIR}`/`${SOLUTION_DIR}` placeholders, bodies over 1 MB, and HTML responses;
  `${ENV_VAR}` still resolves on each consuming machine (the supported way to keep secrets out of
  the shared catalog). New `init --print-central` prints the generated catalog to stdout for
  piping into whatever serves the URL.
- Hot reload of `orchestrator.config.json`: the running orchestrator watches the config file
  (debounced, atomic-rename-aware) and applies edits without a host restart. Invalid edits are
  rejected with an error in the log and the running config is kept (last-known-good). Only
  launch-relevant changes (command, args, env values, working directory, transport, timeouts)
  restart a downstream — in-flight calls drain first, and the new definition connects lazily on
  next use; summary/instructions/enabled edits apply in place. `list_capabilities` reflects the
  new config immediately. On by default; opt out with `MCP_ORCHESTRATOR_NO_RELOAD=1`. The reload
  pipeline is trigger → load + validate → diff + apply, with the file watcher as the first
  pluggable trigger (a polled central config can slot in later).
- `profile` now auto-detects a config when neither `--config` nor `--host-config` is given: it
  looks in the current directory and uses the first of `orchestrator.config.json`, `.mcp.json`,
  `.vscode/mcp.json`, `.cursor/mcp.json`, `mcp.json` that exists (`orchestrator.config.json` is
  profiled directly; the others are imported like `--host-config`). This also supplies the config
  for `--trace`, so `profile --trace session.jsonl` works without naming a config file.
- `init` now auto-detects the host config to adopt when no `<host-config>` argument is given: it
  looks in the current directory and uses the first of `.mcp.json`, `.vscode/mcp.json`,
  `.cursor/mcp.json`, `mcp.json` that exists (the generated `orchestrator.config.json` is init's
  output, so it's never auto-detected as input). So `cd` into a project and run `mcp-orchestrator
  init`.
- `init` now auto-generates each capability's `summary` instead of writing a `TODO` placeholder:
  it connects to each stdio server once (same connection mechanics as `profile`, including the
  connect timeout) and derives the summary from the server's `initialize` `instructions` (first
  sentence, ≤150 chars) or, failing that, its tool names (`"{N} tools for {server}: …"`).
  Deterministic and offline — no LLM. Auto-generated lines are marked with a trailing
  `// auto-generated` comment; a server that fails to start silently keeps the `TODO` placeholder.
  A new `--no-summarize` flag skips the connections entirely for servers that are slow or
  side-effectful to start.

## [0.2.3] - 2026-06-25

### Added
- Pack the registry manifest at `.mcp/server.json` inside the NuGet package. nuget.org reads this
  embedded file to render the "MCP Server" tab and the one-click VS Code configuration; without it
  nuget.org reported "this package does not include a server.json file". Also added
  `registryBaseUrl` to the package entry for parity with the canonical MCP-server package shape.

## [0.2.2] - 2026-06-25

### Added
- `McpServer` NuGet package type (alongside the existing `DotnetTool` type), so the package is
  discoverable under nuget.org's "MCP Server" package-type filter. The tool install path is
  unaffected — both package types ship in the same `.nupkg`.

## [0.2.1] - 2026-06-25

### Added
- Published to the Official MCP Registry (`registry.modelcontextprotocol.io`) as
  `io.github.Byggarepop/dotnet-mcp-orchestrator`, with an automated `mcp-registry` CI job that
  publishes the listing on each version tag via GitHub OIDC. No functional changes to the tool.

## [0.2.0] - 2026-06-24

### Added
- `profile` subcommand that measures the token economics of progressive tool discovery — the delta
  between the naive "load every manifest every turn" baseline and the orchestrator's actual
  progressive cost. Two modes: `profile --config <path>` (static: resting floor, naive baseline, and
  a best/worst envelope where worst is honestly higher than naive) and
  `profile --trace <session.jsonl> --config <path>` (replays a real session into the per-turn curve —
  active vs. naive, load events, never-loaded savings, and break-even, including the honest
  "overhead never repaid" case). `--format json` emits a snake_case superset for tooling, and
  `--assert-favorable` exits non-zero so CI can gate on the orchestrator staying favorable for a
  canonical session.
- `profile --host-config <path>`: a read-only "try before you keep it" path. Points the profiler at
  an existing MCP host config (`.mcp.json` / `.vscode/mcp.json` / Cursor / Claude Desktop) instead of
  an orchestrator config — its stdio servers are imported in memory and measured, **writing
  nothing** (remote http/sse servers are listed and skipped). Run it as a one-shot with
  `dotnet tool execute McpOrchestrator profile --host-config <path>` (or `dnx …`) to see the savings
  with no global install and nothing to uninstall. The host-config parser is shared with `init`.
- Optional session-trace side-channel: run with `--trace-out <path>` (or
  `MCP_ORCHESTRATOR_TRACE_OUT`) to append one JSONL line per discover/route interaction for later
  replay. Off by default; the server hot path is unaffected.
- Local, deterministic token counting via `Microsoft.ML.Tokenizers` (`cl100k_base`, embedded vocab —
  offline and CI-friendly), behind an `ITokenCounter` seam so a live-usage backend can replace it.
  Every report discloses the tokenizer and a ±10% cross-model tolerance.
- `init` subcommand that adopts an existing MCP host config in one step: `mcp-orchestrator init
  <host-config>` lifts every stdio server out of `.mcp.json` / `.vscode/mcp.json` (or any
  `mcpServers` / `servers` map — Cursor, Claude Desktop) into a generated `orchestrator.config.json`
  (one capability each, with a `TODO` `summary` placeholder and no `instructions` — the summary
  drives routing), backs up the host config, then rewrites it to launch only the orchestrator
  pointed at the new catalog via `MCP_ORCHESTRATOR_CONFIG`. Remote (http/sse) servers are left in
  place; the user only fills in the one-line `summary` per capability. `--dry-run` previews both
  files, `--force` overwrites an existing catalog, `--command <path>` targets the AOT binary, and
  `--dev-feed <path>` wires the orchestrator to run from a local folder feed (latest local build).
- `pack-local.ps1`: packs the project as the pinned `9.9.9-dev` version into `nupkg/local-feed` and
  evicts the cached copy, so a host launching the tool with `dotnet tool execute McpOrchestrator
  --version 9.9.9-dev --source <feed> --yes` always runs the latest local code.

### Changed
- `instructions` is now an optional (nullable) capability field, omitted from output and from
  `list_capabilities` when absent, rather than always emitted as an empty string.

## [0.1.1] - 2026-06-22

### Changed
- Renamed the default log folder from `~/.dotnet-orchestrator-mcp` to `~/.mcpOrchestrator`, aligning
  it with the `McpOrchestrator` name. Existing `~/.dotnet-orchestrator-mcp` folders are left in place
  and can be deleted; override with `MCP_ORCHESTRATOR_LOG_DIR` as before.

## [0.1.0] - 2026-06-21

First release.

### Added
- Pure three-tool MCP relay — `list_capabilities` → `discover_tools` → `route` — that forwards the
  agent's calls to downstream MCP servers without interpreting them.
- Config-driven capability catalog (`orchestrator.config.json`) with `${SOLUTION_DIR}`,
  `${CONFIG_DIR}`, and environment-variable placeholders, plus a shipped template.
- Lazy downstream MCP connections over stdio, with per-capability connect/call timeouts and
  eviction of failed connections.
- File logging: the stderr log is mirrored to `~/.mcpOrchestrator/orchestrator.log`
  (folder auto-created, ~10 MB rotation). Override the directory with `MCP_ORCHESTRATOR_LOG_DIR`,
  or disable with `MCP_ORCHESTRATOR_LOG_DIR=off`.
- Native-AOT support: a ~10 MB self-contained binary (no .NET runtime), via source-generated JSON
  and the generic tool registration; `IsAotCompatible` keeps the analyzer on.
- Opt-in self-update for the Native-AOT binary (`MCP_ORCHESTRATOR_AUTOUPDATE=1`): on startup it
  checks the latest GitHub Release, verifies the download against `SHA256SUMS`, and stages the new
  binary so the next launch runs it — without ever restarting the live MCP session.
- Packaging: portable .NET tool package (with the root README as its nuget.org landing page),
  self-contained per-RID tool packages, and self-contained zips (`pack-all.ps1`).
- CI: per-OS Native-AOT build + smoke test (`native-aot.yml`); tag-triggered release workflow
  (`release.yml`) that builds the native binaries (+ `SHA256SUMS`), creates a GitHub Release, and
  publishes the tool package to NuGet via Trusted Publishing (OIDC).
- READMEs link to GitHub Releases for downloading the prebuilt Native-AOT binaries.
