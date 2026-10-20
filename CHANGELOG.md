# Changelog

All notable changes to this project are documented here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

The release workflow reads the section matching the tag (e.g. `## [0.1.0]` for tag `v0.1.0`) and
uses it as the GitHub Release notes — so keep an entry per released version.

## [Unreleased]

### Fixed
- A new session in the GitHub Copilot app could fail every orchestrator call with
  `MCP server "orchestrator" connection was cancelled`. The skills startup pass fetched each `git`
  source before the MCP server started, so the first request waited for the fetch (about 1.2 s for
  a GitHub repo; up to 75 s when another instance held the cache lock). The Copilot app drops an MCP
  connection that has not answered when it creates a new session, and does not reconnect it.
  Startup — and central mode's initial load — now serves each `git` source's cached checkout without
  touching the network or the lock, and fetches right after in the background (log trigger
  `startup-refresh`). With a cached checkout the handshake is ready in about 0.8 s regardless of
  fetch time. On the very first start, before any clone, that session's handshake carries no skills
  hint; the skills arrive once the background clone completes. Shutdown cancels a background fetch
  that is still running.

## [0.7.5] - 2026-10-13

### Added
- The `--on-config-change` hook now reports skill changes. Every run carries
  `MCP_ORCHESTRATOR_CHANGE_SKILLS_ADDED`, `…_SKILLS_REMOVED` and `…_SKILLS_UPDATED` (skill names;
  updated = content hash or source changed) plus `MCP_ORCHESTRATOR_CHANGE_TRIGGER`, and the JSON
  gains `trigger`, `skillsAdded`, `skillsRemoved`, `skillsUpdated`. The hook also runs after a
  skill refresh that the config pipeline did not drive — a `git`/`http` source poll or a
  `directory` source file event — when it changed the served skills; `source` then lists the
  ids of the skill sources involved. The `skill catalog rebuilt` log line ends with the same
  added/removed/updated counts.

### Fixed
- On Linux/macOS the `--on-config-change` hook could report `Config change hook failed` when the
  script exited without reading stdin: closing the pipe afterwards raised a broken-pipe error.
  The close is now guarded like the write.

## [0.7.4] - 2026-10-13

### Added
- `--on-config-change <script>` (or `MCP_ORCHESTRATOR_ON_CONFIG_CHANGE`): run a project script
  after every applied config reload, from a local file edit or a changed central URL alike. The
  interpreter follows the extension (`.ps1`, `.py`, `.sh`, `.cmd`/`.bat`; other files run
  directly), the diff arrives as `MCP_ORCHESTRATOR_CHANGE_*` environment variables and as JSON on
  stdin, output lands in the orchestrator log as `[hook]` lines, and a run is killed after
  `MCP_ORCHESTRATOR_ON_CONFIG_CHANGE_TIMEOUT_SECONDS` (default 120). Not run at startup or for a
  rejected reload; a failing script never undoes the reload.

## [0.7.3] - 2026-10-11

### Fixed
- Downstream servers built on an older MCP SDK connect again. Since 0.6.1 the orchestrator pins
  its downstream clients to protocol revision 2025-11-25 (to skip the `server/discover` probe and
  its warning flood), but the SDK treats that pin as a minimum: a server that answers `initialize`
  with 2025-06-18 (TokenSaver 1.15.x, for example) was refused with "Server protocol version
  mismatch", so `discover_tools` returned no schemas. The orchestrator now reconnects pinned to
  the server's revision when it is one the SDK speaks over `initialize` (2024-11-05 through
  2025-11-25), logging `speaks MCP protocol <old> rather than <pinned>; reconnecting at its
  revision`. The probe is still never issued, so the earlier noise does not return. A revision the
  SDK cannot speak is still a clear error.

## [0.7.2] - 2026-10-11

### Fixed
- Git skill sources no longer hang on every clone and fetch under a stdio host. Git inherited the
  orchestrator's stdin, which is the MCP pipe the transport is always reading, and Git for Windows
  blocks at startup on such a pipe, so each sync ran into the 60 s timeout without ever contacting
  the remote. Git now gets its own stdin, closed at once.
- The git skill source's `token` now authenticates as HTTP basic auth (`x-access-token:<token>`),
  the form GitHub, GitLab and Azure DevOps document for tokens over HTTPS. It was sent as
  `Authorization: Bearer`, which GitHub's git endpoints reject even for a valid token, so every
  clone of a private repo got a 401 and fell through to the machine's credential helper.
- With a token configured, git's credential helpers are switched off for the sync. Previously a
  rejected token handed the request to the helper, which either waited for a login nobody could
  see (the clone then hit the 60 s timeout) or erased the user's stored GitHub credential because
  the server had answered 401.
- Git Credential Manager's `credential.interactive=never` is passed as git config as well as the
  environment switch, and the timeout warning now says the likely cause (a credential prompt) and
  what to check.

### Added
- `clone started` / `fetch started` log lines carry `auth=token` or
  `auth=none (git credential helper)`.

## [0.7.1] - 2026-10-11

### Fixed
- Git skill sources now hold a lock file per cache folder, so several orchestrator processes
  (hosts start one per window and restart them all on a config change) no longer race into the
  same clone: the first instance clones, the others wait and reuse its checkout. Previously the
  losers failed with `destination path … already exists and is not an empty directory`.
- Git runs without credential prompts (`GIT_TERMINAL_PROMPT=0`, `GCM_INTERACTIVE=never`, askpass
  off). A private repo with no stored credential or `token` now fails within a second with the git
  error and a log line naming the fix, instead of hanging until the 60-second timeout killed it.
- A cache folder with content but no `.git` (an interrupted clone) is cleared before cloning
  rather than making every later clone fail.
- Local git probes (`rev-parse`) use a 10-second timeout instead of 60.

## [0.7.0] - 2026-10-11

### Changed
- The initialize handshake now reserves the essential guidance first — routing header, a short
  skills-discovery hint (`list_skills` / `get_skill` / `get_skill_file`) and one line per
  capability — and spends only the remainder of the budget on promoted instructions. Previously
  the skills line was appended last and was dropped when capability text filled the budget. The
  hint is omitted when no skills are served or `delivery.catalogTools` is off; if the base
  itself overflows, summaries are shortened (with a warning) rather than any line dropped.
- The handshake budget is raised from 1,900 to 3,800 characters after re-measuring the hosts
  (2026-10-11): Claude Code 2.1.296 keeps 4,096 characters (about 2,048 in July), GitHub Copilot
  CLI 1.0.70 passes 6,000 whole once the server is allowlisted for instructions, and VS Code's
  Copilot agent applies no fixed cap. The README records the probe method.
- Skill refresh logging now shows every stage: config change detected, sources added/removed/
  changed (credentials reported as changed, never by value), git clone/fetch started (ref, cache
  path, timeout) and completed (duration, commit; `checkout unchanged` vs `checkout updated
  <old> -> <new>`), per-source contribution marked fresh or cached, and the rebuild trigger
  (`startup`, `config-change`, `directory-change`, `scheduled-refresh`). A failed fetch states
  whether the cached checkout keeps serving and when the next refresh is due. Clone URLs are
  logged with userinfo redacted.

### Documentation
- New "Reading the log" section in `docs/skills.md`.

## [0.6.2] - 2026-10-11

### Fixed
- A skill source whose `${VAR}` placeholder has no value on the machine is now skipped with a
  warning naming the variable, instead of scanning a literal `${...}` path. This makes a central
  catalog with a per-project `directory` source (`"path": "${PROJECT_SKILLS_DIR}"`) work cleanly:
  projects that set the variable get the skills, the rest skip the source.

### Documentation
- New "Central catalog with per-project skills" section in `docs/skills.md`, a pointer from the
  migration guide, and a `project-skills` source in the central example config.

## [0.6.1] - 2026-10-10

### Fixed
- Downstream connections no longer start with a rejected `server/discover` probe. The MCP C# SDK
  2.0 opens every connection by probing for the 2026-07-28 revision; servers that predate it
  answer with an error (`Method not found` / `Invalid request parameters`), which the orchestrator
  log showed as a warning for every capability and, for Python servers, as a block of
  `validation errors for ClientRequest` on stderr. The SDK fell back to `initialize` and the
  connection worked, so nothing was broken, but the noise looked like a failure. The orchestrator
  now pins its downstream clients to the `2025-11-25` revision, which skips the probe entirely.
  Revisit if an HTTP transport is ever added (see the comment on `DownstreamProtocolVersion`).
- The fallback per-process log (`orchestrator-<pid>.log`, used when another orchestrator already
  holds `orchestrator.log`) is truncated on start instead of appended. Windows reuses process
  IDs, so a new run could land under an old session's entries. Every log now also opens with a
  session banner (version, time, PID) so runs appended to the shared log can be told apart.

## [0.6.0] - 2026-10-10

### Fixed
- Downstream servers now receive MCP **roots**. The orchestrator advertised no `roots`
  capability to any downstream, so servers that scope file output to the client's workspace
  (chrome-devtools-mcp — verified on 1.9.0 and 1.10.1 — restricts `take_screenshot` et al. to the OS temp folder in that
  case) rejected every path inside the project with "Access denied: path ... is not within any
  of the configured workspace roots." Each capability now advertises `roots` and answers
  `roots/list` with its own `workingDirectory` (or the new optional `roots` list) as absolute
  `file:` URIs — never the orchestrator's own working directory, and nothing at all for a
  capability with neither.

### Changed
- Tool results are now serialized **compact** (no indentation) instead of pretty-printed. Every
  string the orchestrator hands back to the model — `list_capabilities`, `discover_tools`,
  `route`, the structured errors, and the skills catalog tools — went through a serializer with
  `WriteIndented = true`, so each nesting level cost two spaces and a CRLF per line of
  model-visible text. Config files and the `init` scaffolding are unaffected; only the
  model-facing text changed, and the JSON content is identical. Measured on the demo catalog
  plus the repo's two skills (chars of the returned text; tokens scale with it):

  | Tool result | Indented | Compact | Saved |
  |---|---|---|---|
  | `list_capabilities` (3 capabilities) | 292 | 224 | 23% |
  | `discover_tools` jira (2 tools with schemas) | 966 | 616 | 36% |
  | `discover_tools` codegen (1 tool) | 767 | 468 | 39% |
  | `route` get_issue | 339 | 301 | 11% |
  | `route` search_issues | 718 | 680 | 5% |
  | `route` with text + structured copy | 594 | 465 | 22% |
  | `route` unknown-capability error | 164 | 130 | 21% |
  | `list_skills` (2 skills) | 454 | 408 | 10% |
  | `get_skill` (3.6 KB SKILL.md body) | 3613 | 3576 | 1% |
  | Total over 11 calls | 9275 | 8154 | 12% |

  The saving is largest where the orchestrator itself emits structure (tool schemas in
  `discover_tools`, the `structured` field of `route`) and negligible where the payload is one
  long string (a skill body).

### Added
- Optional per-capability `roots` config field (supports `${VAR}`); a change to it restarts
  that downstream on hot reload like any other launch-relevant field.
- `enabled` accepts a `${VAR}` string (e.g. `"${USE_DATABASE:-false}"`), resolved from the
  environment like every other substitutable field, so one centrally served catalog can switch
  capabilities on or off per machine. Boolean `true`/`false` keep working unchanged.
- `${VAR:-default}` placeholder syntax: falls back to `default` when `VAR` is unset. The default
  may itself be a placeholder (`${USERPROFILE:-${HOME}}`), so one catalog serves Windows and
  Unix machines; the committed central example uses this for its `files` entry.
- Central mode: an enabled capability with an unresolved placeholder is now disabled with an
  error naming the capability, the variable, and where to set it, instead of launching the
  downstream with the literal `${VAR}` text. Only that capability is affected; the rest of the
  catalog stays live and the next poll re-enables it once the variable exists. Local-file mode
  is unchanged (unresolved placeholders are left as-is with a warning).
- `docs/central-migration.md`: how to convert an existing setup to a central catalog with
  per-project values, including version compatibility and rollback.

## [0.5.2] - 2026-08-08

### Fixed
- `route` no longer swallows downstream failure detail on any path. Previously only
  protocol-level faults were relayed with attribution; a downstream tool that *returned* an
  error result kept its raw text — which, for servers built on the MCP C# SDK, is the
  genericized "An error occurred invoking '&lt;tool&gt;'." with the real exception logged only to
  the server process's stderr, invisible to the calling model. Now every failure path — error
  results, protocol faults, timeouts, connect failures — returns
  `Downstream capability '<name>' tool '<tool>' failed: <verbatim downstream message>` plus a
  `stderr` field carrying the lines the downstream process wrote during the failing call (e.g.
  `System.ArgumentException: The arguments dictionary is missing a value for the required
  parameter 'repoPath'`), so the model can self-correct without access to host logs.

## [0.5.1] - 2026-08-08

### Added
- Actionable tool-call validation: every tools/call is checked against the target tool's input
  schema before SDK argument binding, so a call with missing, unknown, or wrongly-typed
  parameters returns an error naming the exact problem and the expected shape (e.g.
  `Missing required parameter 'arguments' (received unknown parameter 'argz'). Expected shape:
  {capability: string, tool: string, arguments: object}.`) instead of the SDK's generic
  "An error occurred invoking 'route'". Predictable parameter-name synonyms are accepted as
  aliases — `args`/`params`/`parameters`/`input`/`payload` for `arguments`, `tool_name` for
  `tool`, `skill` for `name`, and similar — with the rewrite logged and a note appended to the
  result so the model learns the canonical name.
- The initialize-handshake server instructions now open with a literal `route` example —
  `{"capability":"<name>","tool":"<tool>","arguments":{"key":"value"}}` — and an explicit note
  that the parameter is `arguments`, not `args`.
- Verified interop with Microsoft Agent Framework's MCP skill discovery
  (`AgentSkillsProviderBuilder.UseMcpSkills`): the provider discovers the orchestrator's
  skills through the `skill://index.json` convention with no server-side changes. Documented
  in the README and `docs/skills.md`, with a runnable probe in `McpOrchestrator.InteropTest/`.

### Changed
- A protocol-level failure from a proxied server is now relayed with attribution —
  `Downstream capability '<name>' tool '<tool>' failed: <the downstream server's actual error
  text>` — instead of the bare exception message.
- A CLI reference (`docs/cli.md`): every command, flag, and environment variable of the tool —
  the server, `init`, and `profile` — in one linkable page, generated from the commands'
  built-in help.
- A `publish-release` skill (`docs/skills/publish-release/`): the complete McpOrchestrator
  release procedure as an Agent Skill — preconditions, the two-file bump PR, tagging, deploy
  verification, and a reference file of real failure modes — so an agent can run a release
  correctly without rediscovering the process.

## [0.5.0] - 2026-07-31

### Added
- Skills-over-MCP: the orchestrator can serve Agent Skills (SKILL.md folders per the
  agentskills.io spec) from a new `skills` config section, in two delivery modes. Mode A
  (default on) is the compact catalog tool trio — `list_skills` (names + one-line
  descriptions), `get_skill` (the SKILL.md body + file list), `get_skill_file` (one
  supporting file, strict path validation) — following the same progressive-disclosure
  economics as the capability catalog; an optional `perSkillTools` flag (default off)
  exposes one tool per skill. Mode B (default on) exposes each skill file as an MCP
  Resource under `skill://<name>/<path>` plus a `skill://index.json` catalog per the
  pending SEP-2640 proposal, with every SEP convention isolated in one class since the
  proposal is still in draft. Skills load from three source types — `directory`
  (recursive discovery, live file watcher), `git` (shallow clone via the git CLI,
  private repos via a `token` sent as an Authorization header), and `http` (discovery
  index) — into immutable in-memory snapshots, so serving never touches the origin and
  path traversal is structurally impossible. Governance: `allowedSkills`/`deniedSkills`
  (deny wins), deterministic per-skill SHA-256 integrity pinning with `warn`/`block`
  modes, and an audit log line (skill, file, content hash, source, delivery mode) for
  every served item. The skills section rides the existing hot-reload pipeline in both
  file-watch and central-config modes; skill content reloads independently with atomic
  catalog swap and last-known-good on failure. Skills are served as files, never
  executed. Docs: `docs/skills.md`, a sample skill at `docs/skills/release-notes/`, and
  skills sections in the sample/template/central example configs.

### Changed
- Upgraded the MCP C# SDK (`ModelContextProtocol` + `.Core`) from 1.4.0 to 2.0.0,
  which targets the final 2026-07-28 MCP specification. Wire-compatible with older
  peers; verified against the full test suite, a Native-AOT publish, and the native
  smoke test.
- READMEs reworked agent-first and slimmed to the short-README-linking-to-docs pattern:
  the root README (also the NuGet package page) is now a compact landing page whose
  quick start shows the generated `orchestrator.config.json` and includes an
  "Add a skill" walkthrough; manual setup moved to `docs/manual-setup.md`, the skills
  guide to `docs/skills.md`, and contributor documentation (build/test/extend/debug)
  out of the user docs into `docs/development.md`.

## [0.4.1] - 2026-07-16

### Added
- Project logo and social preview images (`McpOrchestrator/img/`). The logo is packed into the
  NuGet package and set as its icon on nuget.org; the README opens with the social preview banner
  (served via an absolute GitHub URL so it renders on nuget.org too).

### Security
- Pinned the transitive `Microsoft.Bcl.Memory` (pulled in via the MCP SDK chain) to 10.0.10,
  moving off 9.0.4 which has a known high-severity vulnerability (GHSA-73j8-2gch-69rq / NU1903).

## [0.4.0] - 2026-07-13

### Added
- Session-start catalog advertisement, fixing proactive-capability discoverability. The catalog
  used to be pull-only (visible only in a `list_capabilities` result), so a capability the agent
  should call unprompted was never triggered — nothing in the agent's context mentioned it. Now
  the MCP initialize handshake's server instructions list every enabled capability's name +
  summary, and capabilities marked with the new per-capability `promote` flag (default `false`)
  get their full `instructions` hoisted in as well (capped at 2,000 chars each, with a pointer to
  `list_capabilities` for the full text). The `list_capabilities` tool description also gains a
  generated "Currently registered: …" suffix. `list_capabilities`/`discover_tools`/`route`
  behavior is unchanged; the advertisement is a per-session snapshot (in central mode, taken
  after the initial fetch). The committed central example catalog promotes `Unwritten`.
- The advertisement block as a whole is budgeted at 1,900 characters (Claude Code was observed
  to truncate rendered server instructions at ~2,048). Spent in priority order — header and all
  name/summary lines first, then promoted instructions in catalog order; the first entry that
  doesn't fit is truncated with the `list_capabilities` pointer, later promoted entries are
  omitted, and a startup warning names the affected capabilities. The example catalog's
  `Unwritten` entry was tightened to fit the budget whole.

## [0.3.0] - 2026-07-02

### Changed
- `init` now writes an install-free host entry by default: the orchestrator is launched via
  `dotnet tool execute McpOrchestrator --version <the version init ran as> --yes` (resolved from
  the local NuGet cache — no global install needed, nothing to go stale). `--command` still
  overrides it (e.g. a globally installed `mcp-orchestrator` or the AOT binary path), and
  `--dev-feed` is unchanged. The quick start is now two steps with no install step.

### Added
- `init --central-url <url>`: join a centrally served catalog — rewrites the host config so the
  orchestrator entry carries `MCP_ORCHESTRATOR_CONFIG_URL` instead of a local config path. Writes
  no catalog file and contacts no servers; stdio servers are lifted out of the host config as
  usual and listed so the user can verify the central catalog covers them. The URL is validated
  with the same HTTPS rule as the runtime, and credentials are never written to the host config
  (the summary points at `MCP_ORCHESTRATOR_CONFIG_AUTH` instead).
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
