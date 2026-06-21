# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow reads the section matching the tag (e.g. `## [0.1.0]` for tag `v0.1.0`) and
uses it as the GitHub Release notes — so keep an entry per released version.

## [Unreleased]

## [0.1.0] - 2026-06-21

First release.

### Added
- Pure three-tool MCP relay — `list_capabilities` → `discover_tools` → `route` — that forwards the
  agent's calls to downstream MCP servers without interpreting them.
- Config-driven capability catalog (`orchestrator.config.json`) with `${SOLUTION_DIR}`,
  `${CONFIG_DIR}`, and environment-variable placeholders, plus a shipped template.
- Lazy downstream MCP connections over stdio, with per-capability connect/call timeouts and
  eviction of failed connections.
- File logging: the stderr log is mirrored to `~/.dotnet-orchestrator-mcp/orchestrator.log`
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
