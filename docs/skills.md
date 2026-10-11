# Agent Skills over MCP

The orchestrator can serve **[Agent Skills](https://agentskills.io/specification)** — folders with
a `SKILL.md` (YAML frontmatter: `name`, `description`; markdown body: the instructions) plus
optional `references/`, `scripts/`, and `assets/` — from local folders, git repositories, or an
HTTP(S) index. Same design principle as the rest of the tool: the catalog costs a name + one line
per skill; full content loads only on demand. Skills are **served as files, never executed**.

Contents:
[Quick start](#quick-start) ·
[Configuration](#configuration) ·
[Sources](#sources) ·
[Private git repos](#private-git-repos--do-i-need-a-token) ·
[Delivery modes](#delivery-modes) ·
[Governance](#governance) ·
[How it works](#how-it-works)

> **Agent-only, by design.** Skills served over the orchestrator are consumed by the *model*
> through tools — they do **not** register as host-native skills. They won't appear in Claude
> Code's `/skills` list, get no slash command, and won't pop up in any IDE skill picker: the
> host just sees an MCP server with tools, and the agent discovers the catalog itself via
> `list_skills`. If you want a skill as a native `/command` too, install it in the host's own
> location (e.g. `~/.claude/skills/`) — syncing orchestrator-served skills into client-native
> formats automatically is a planned follow-up.

## Quick start

Skills are used *by the agent* — you write a folder, the agent finds and follows it. Three steps:

1. **Write a skill**: a folder whose name matches the frontmatter `name`, containing a `SKILL.md`
   with a `name`, a `description` (this is what the agent matches tasks against — make it say
   *when* to use the skill), and the instructions as the markdown body. Optional supporting files
   go in `references/`, `scripts/`, `assets/`. A complete example:
   [`docs/skills/release-notes/`](skills/release-notes/).
2. **Point the orchestrator at it** in `orchestrator.config.json`:

   ```jsonc
   "skills": {
     "sources": [{ "id": "local", "type": "directory", "path": "C:/my-skills" }]
   }
   ```

   Save — the running orchestrator hot-reloads it, and later edits to skill files are picked up
   live as well.
3. **Let the agent use it.** Nothing to trigger manually: the session greeting mentions the skill
   catalog, and when a task matches a skill's description the agent chains
   `list_skills` → `get_skill` → `get_skill_file` on its own, loading full content only when
   needed.

**Verifying without an agent** (optional): the MCP Inspector can drive the same tools by hand —
`npx @modelcontextprotocol/inspector -e MCP_ORCHESTRATOR_CONFIG=<path-to-config> mcp-orchestrator`
(the `-e` matters: the Inspector does not forward your shell's environment to the server it
spawns). On connect, the server's stderr shows `skill catalog rebuilt: N skill(s) served; trigger=startup; …`
(see [Reading the log](#reading-the-log)); an empty `list_skills` result means the server loaded a
different config than you intended.

## Configuration

```jsonc
"skills": {
  "enabled": true,
  "sources": [
    { "id": "local",  "type": "directory", "path": "${CONFIG_DIR}/skills" },
    { "id": "team",   "type": "git",  "url": "https://github.com/org/skills.git",
      "ref": "main", "token": "${SKILLS_GIT_TOKEN}", "pollSeconds": 300 },
    { "id": "cdn",    "type": "http", "indexUrl": "https://skills.example.com/index.json",
      "authorization": "${SKILLS_HTTP_AUTH}" }
  ],
  "delivery": { "catalogTools": true, "perSkillTools": false, "resources": true },
  "governance": {
    "allowedSkills": [], "deniedSkills": [],
    "integrity": { "mode": "warn", "sha256": { "some-skill": "9f2c…" } }
  }
}
```

Every string field accepts `${VAR}` / `${VAR:-default}` placeholders, resolved from the
orchestrator's environment (see [Per-project values](../McpOrchestrator/README.md#per-project-values-in-a-shared-catalog)).

| Field | Applies to | Meaning |
| --- | --- | --- |
| `enabled` | section | Master switch. `false` registers nothing. Default `true`. |
| `sources[]` | section | Where skills come from, in priority order: on a name collision the earlier source wins. |
| `id` | every source | Label for the source in log lines, audit lines and the git cache folder name. Must be unique in the config. Never shown to agents. |
| `type` | every source | `directory`, `git` or `http`. |
| `path` | `directory` | Folder scanned recursively for `SKILL.md` files. |
| `url` | `git` | Clone URL (https). |
| `ref` | `git` | Branch, tag or commit to check out. Default: the remote default branch. |
| `token` | `git` | Bearer token for private repos, sent as an `Authorization` header. Never logged. |
| `indexUrl` | `http` | URL of an [Agent Skills discovery index](https://agentskills.io) listing the skills and their files. |
| `authorization` | `http` | Verbatim `Authorization` header value for the index and file requests. Never logged. |
| `pollSeconds` | `git`, `http` | Refresh interval. Default 300, minimum 10. Ignored for `directory`, which uses a file watcher. |
| `delivery.catalogTools` | delivery | Expose `list_skills` / `get_skill` / `get_skill_file`. Default `true`. |
| `delivery.perSkillTools` | delivery | Also expose one `skill_<name>` tool per skill. Default `false`: every tool costs context on every turn. |
| `delivery.resources` | delivery | Expose `skill://` resources and `skill://index.json` (SEP-2640). Default `true`. |
| `governance.allowedSkills` | governance | Skill names that may be served. Empty means all. |
| `governance.deniedSkills` | governance | Skill names never served. Deny wins over allow. Case-insensitive. |
| `governance.integrity.mode` | governance | On a hash mismatch: `warn` (serve and log, default) or `block` (treat as denied). |
| `governance.integrity.sha256` | governance | Skill name to expected SHA-256 of the skill folder content. |

## Sources

`directory` scans recursively for `SKILL.md` files (a `FileSystemWatcher` picks up edits live).
`git` shallow-clones via the `git` CLI into `~/.mcpOrchestrator/skills-cache/` and re-fetches
every `pollSeconds` (default 300) — only the latest files, no history, and the cache is a
disposable mirror that is hard-reset on every refresh. `http` fetches a discovery index (the
[Agent Skills discovery format](https://agentskills.io); entries may add a `files` array to
enumerate supporting files — plain HTTP has no directory listing). Invalid skills are logged and
skipped, never fatal; on a name collision the earlier source in config order wins. A skills
section in a [central config](../McpOrchestrator/README.md#central-configuration) works too
(see [Central catalog with per-project skills](#central-catalog-with-per-project-skills)), and
config edits hot-reload like everything else.

**Which source type do I want?** `directory` for skills on this machine (simplest — start here).
`git` for a team-shared skills repo: someone merges a skill improvement, and every orchestrator
polling that repo serves it within `pollSeconds` — review and versioning come free via normal
PRs. `http` for published skill sets behind a plain URL. All three end up in the same in-memory
snapshots and are served identically.

## Central catalog with per-project skills

One central config can carry both the team's shared skills and a slot for skills that live in
each project. The central file names the sources; every `${VAR}` in them is resolved on the
consuming machine from the orchestrator's env block, exactly like capabilities.

1. In the central config, list the shared sources and one `directory` source whose path is a
   variable:
   ```jsonc
   "skills": {
     "sources": [
       { "id": "team",    "type": "git",       "url": "https://github.com/org/skills.git", "ref": "main" },
       { "id": "project", "type": "directory", "path": "${PROJECT_SKILLS_DIR}" }
     ]
   }
   ```
2. In each project's `.mcp.json`, set the variable in the orchestrator's env block:
   ```jsonc
   "env": {
     "MCP_ORCHESTRATOR_CONFIG_URL": "https://devops.example.com/mcp/orchestrator.config.json",
     "PROJECT_SKILLS_DIR": "C:/ProjectA/skills"
   }
   ```
3. Projects that do not set the variable skip that source with a log line naming it; the shared
   sources still load. A default such as `"${PROJECT_SKILLS_DIR:-C:/shared/skills}"` keeps the
   source on every machine instead.

Rules: `${CONFIG_DIR}` and `${SOLUTION_DIR}` are rejected in a central config (they are
machine-local); use `${ENV_VAR}` or absolute paths. Order decides collisions — put the `project`
source first if a project must be able to override a team skill of the same name, last if the
team version should always win.

## Private git repos — do I need a token?

Only if `git clone <url>` wouldn't already work on that machine:

1. **Public repo** → no token, just the URL.
2. **Your dev machine with stored git credentials** (e.g. Git Credential Manager) → no token; the
   orchestrator shells out to your `git`, so its *stored* credentials are used. It runs git with
   credential prompts disabled (it has no terminal, and a prompt would hang the sync until the
   timeout kills it), so the credential must already be saved: if `git clone <url>` succeeds in a
   terminal *without asking*, the same URL works in the config. If git asks, clone once in a
   terminal to store the credential, or use a token (next item). A refused or missing credential
   shows in the log as `git could not authenticate to <url>` with the git error above it.
3. **No stored credentials** (CI, servers, a shared central config) → create a read-only token
   (GitHub: Settings → Developer settings → *Fine-grained personal access token*, scoped to the
   skills repo, permission **Contents: Read-only**), set it as an environment variable (e.g. in
   the orchestrator's `env` block: `"SKILLS_GIT_TOKEN": "github_pat_…"`), and reference it as
   `"token": "${SKILLS_GIT_TOKEN}"`. The config file stays free of secrets — each machine
   resolves its own variable. The token is sent as an `Authorization` header, never put on the
   URL or logged; if it expires, the last fetched copy keeps serving with a warning in the log.

## Delivery modes

**Mode A — catalog tools** (`catalogTools`, default on, works with every MCP client):
`list_skills` (names + one-line descriptions) → `get_skill` (the SKILL.md body + file list) →
`get_skill_file` (one supporting file; strict path validation, text inline / binary as base64).
`perSkillTools` (default **off**) additionally exposes one tool per skill — that inflates every
session's context, which is exactly what this tool exists to avoid; prefer the catalog trio.

**Mode B — resources** (`resources`, default on): each skill file is an MCP Resource at
`skill://<name>/<path>`, plus a `skill://index.json` catalog resource, following
**[SEP-2640](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2640)**. SEP-2640
is a *pending proposal* still under review; every URI/format convention is isolated in
`Sep2640Conventions.cs` so a spec change is a small diff. Few MCP hosts surface resources to the
model directly (in Claude Code, type `@` to attach one manually), but this is the mode
skill-aware frameworks consume: **[Microsoft Agent Framework](https://devblogs.microsoft.com/agent-framework/discover-agent-skills-from-mcp-servers-in-net/)**
discovers skills through exactly this convention via
`AgentSkillsProviderBuilder.UseMcpSkills(mcpClient)`. Verified against the orchestrator
(August 2026, `Microsoft.Agents.AI.Mcp 1.17.0-alpha`): pointed at an orchestrator serving this
repo's skills, the provider discovers every published skill as type `skill-md`, loads the
SKILL.md bodies, lists `references/` files as skill resources, and injects the
`<available_skills>` catalog plus its `load_skill` / `read_skill_resource` tools — no
orchestrator-side changes needed.

> **SEP-2640 status (July 2026).** This implementation tracks the *published working-group
> draft* in [`modelcontextprotocol/experimental-ext-skills`](https://github.com/modelcontextprotocol/experimental-ext-skills)
> (skills as plain Resources + a well-known `skill://index.json` catalog). The head of the
> [SEP-2640 PR](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2640) has since
> evolved past that draft — it currently proposes dedicated `skills/list` / `skills/get` methods
> with per-file digest manifests instead of the index resource — and is still changing. We
> deliberately stay on the published draft until the SEP merges, then adapt once. If you are
> comparing behavior against the PR text, expect that difference. Mode A (the catalog tools) is
> plain MCP and unaffected by any of this.

## Governance

`deniedSkills` beats `allowedSkills` (empty allow-list = allow all). Integrity pinning: map a
skill name to the SHA-256 of its folder content — on mismatch `warn` (serve + log, default) or
`block` (drop). The hash is deterministic: files sorted by `/`-normalized relative path, each
hashed as `path 0x00 content 0x00` into one SHA-256 (see `SkillHasher` for a shell one-liner).
Every served skill body or file is audit-logged (skill, file, hash, source, delivery mode) to
stderr and the [log file](../McpOrchestrator/README.md#logs).

**Example.** A team repo holds many skills, but this orchestrator should serve only the three
that its projects use, never the experimental `deploy-prod` skill even if someone adds it to the
allow-list, and the `release-checklist` skill must match a reviewed version:

```jsonc
"governance": {
  "allowedSkills": ["code-review", "release-checklist", "pdf-processing"],
  "deniedSkills":  ["deploy-prod"],
  "integrity": {
    "mode": "block",
    "sha256": { "release-checklist": "9f2c3a…" }
  }
}
```

The result: `list_skills` shows at most those three names. Any other skill in any source is
discovered, logged as dropped, and never served. If the `release-checklist` folder changes
upstream, its hash no longer matches and `block` drops it until the pin is updated to the new
reviewed hash, so an unreviewed edit cannot reach agents. With `"mode": "warn"` it would be
served anyway, with a warning in the log.

Governance is global, not per source: the lists apply to the merged catalog after collisions are
resolved. To let a project add its own skills under a central allow-list, either leave
`allowedSkills` empty and rely on `deniedSkills`, or include the project skill names in the list.

## How it works

```
config "skills" section          triggers: file watcher (directory) / poll timer (git, http)
        │                                      │
        ▼                                      ▼
DirectorySkillSource ─┐
GitSkillSource       ─┼─► SkillSnapshots ─► governance ─► SkillCatalog ─► SkillRegistry (atomic swap)
HttpSkillSource      ─┘   (every file in     (allow/deny,                       │
                           memory + SHA-256)  hash pins)         ┌──────────────┴──────────────┐
                                                          mode A: 3 catalog tools      mode B: skill:// resources
```

The core idea is the **immutable snapshot**: each source materializes every skill completely into
memory — frontmatter identity, body, all file bytes, one deterministic folder hash. Serving never
touches disk or network; a file request is a dictionary lookup, which is why path traversal is
structurally impossible, why the audit hash describes exactly the bytes served, and why directory,
git, and HTTP sources behave identically once loaded.

Any trigger (file event, poll tick, config change) rebuilds all sources into a complete new
catalog, then swaps a single reference — readers never lock, in-flight requests finish against the
old catalog, and a failed rebuild keeps the last good one (same last-known-good philosophy as the
capability config reload).

### Reading the log

Each stage of a refresh is its own line, so a change can be followed from detection to the served
catalog without guessing, and a fresh download is distinguishable from a reused cache even when
the skill count is the same. A central config edit that renames a git source looks like this
(stderr and the [log file](../McpOrchestrator/README.md#logs); shortened):

```
info Config change detected: https://…/orchestrator.central.json; reload starting.
info skills config apply starting; trigger=config-change; 2 source(s) configured (git3:git, project-skills:directory)
info skill source removed: git2
info skill source added: git3 (git)
info skill source git3: clone started; url=https://github.com/org/skills.git; ref=main; cache=…\skills-cache\git3; timeout=60s
info skill source git3: clone completed; duration=1840ms; commit=9f2c3a…
info skill source git3: 26 skill(s) loaded from fresh content
info skill source project-skills: 2 skill(s) loaded from fresh content
info skill catalog rebuilt: 28 skill(s) served; trigger=config-change; sources: git3=26 fresh content, project-skills=2 fresh content (…)
info Config reloaded from https://…: 0 added, 0 removed, …
```

- **Trigger** is one of `startup`, `config-change` (file watcher or central poll), `directory-change`
  (a file event under a `directory` source) or `scheduled-refresh` (a git/http poll tick).
- **Source changes** are reported per id before any slow work: added, removed, or changed with the
  differing fields (`path`, `url`, `ref`, `indexUrl`, `pollSeconds`). A token or authorization
  change is reported as `token changed`, never by value; URLs are logged with any userinfo removed.
- **Git refreshes** log `fetch started`, then `refresh completed; checkout unchanged; commit=…` or
  `refresh completed; checkout updated; <old> -> <new>`. A timed-out or failed fetch logs the git
  error and then either `serving the cached checkout at commit …; next refresh in Ns` (last-known-good)
  or `no usable checkout …; source yields no skills` — never a success-shaped line.
- **Several orchestrator processes** (hosts run one per window and restart them all on a config
  change) share the cache folder under a lock file. The first one clones; the others log
  `another orchestrator instance is syncing …; waiting for it`, then `fetch started` against the
  finished checkout. A cache folder with content but no `.git` (an interrupted clone) is logged
  as `clearing it before cloning`.
- **Contribution** per source says `fresh content`, `cached content (origin unreachable)` or
  `nothing (origin unreachable, no cache)`, and the `rebuilt` line repeats them.

Session startup also logs whether the skills-discovery hint made it into the handshake:
`Advertising N capabilities in the initialize handshake (… skills hint included, 1612 chars)`.
