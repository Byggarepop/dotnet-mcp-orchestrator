# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow reads the section matching the tag (e.g. `## [0.1.0]` for tag `v0.1.0`) and
uses it as the GitHub Release notes — so keep an entry per released version.

## [Unreleased]

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
- Optional session-trace side-channel: run with `--trace-out <path>` (or
  `MCP_ORCHESTRATOR_TRACE_OUT`) to append one JSONL line per discover/route interaction for later
  replay. Off by default; the server hot path is unaffected.
- Local, deterministic token counting via `Microsoft.ML.Tokenizers` (`cl100k_base`, embedded vocab —
  offline and CI-friendly), behind an `ITokenCounter` seam so a live-usage backend can replace it.
  Every report discloses the tokenizer and a ±10% cross-model tolerance.

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
