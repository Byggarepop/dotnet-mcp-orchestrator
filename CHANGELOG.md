# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow reads the section matching the tag (e.g. `## [0.1.0]` for tag `v0.1.0`) and
uses it as the GitHub Release notes — so keep an entry per released version.

## [Unreleased]

### Added
- Opt-in self-update for the Native-AOT binary (`MCP_ORCHESTRATOR_AUTOUPDATE=1`): on startup it
  checks the latest GitHub Release, verifies the download against `SHA256SUMS`, and stages the new
  binary so the next launch runs it — without ever restarting the live MCP session.
- The release workflow now publishes a `SHA256SUMS` asset for the native binaries.
- NuGet publishing via Trusted Publishing (OIDC) — no stored API key; nuget.org issues a
  short-lived key to the release workflow. Opt-in (`PUBLISH_NUGET=true` + a `NUGET_USER` secret and
  a nuget.org trusted-publishing policy).

## [0.1.0] - 2026-06-21

### Added
- Pure three-tool MCP relay — `list_capabilities` → `discover_tools` → `route` — that forwards the
  agent's calls to downstream MCP servers without interpreting them.
- Config-driven capability catalog (`orchestrator.config.json`) with `${SOLUTION_DIR}`,
  `${CONFIG_DIR}`, and environment-variable placeholders, plus a shipped template.
- Lazy downstream MCP connections over stdio, with per-capability connect/call timeouts and
  eviction of failed connections.
- Native-AOT support: a ~10 MB self-contained binary (no .NET runtime), via source-generated JSON
  and the generic tool registration; `IsAotCompatible` keeps the analyzer on.
- Packaging: portable .NET tool package, self-contained per-RID tool packages, and self-contained
  zips (`pack-all.ps1`).
- CI: per-OS Native-AOT build + smoke test (`native-aot.yml`); tag-triggered release workflow that
  builds the native binaries and creates a GitHub Release (`release.yml`).
