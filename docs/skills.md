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
spawns). On connect, the server's stderr shows `skill catalog rebuilt: N skill(s) served (…)`; an
empty `list_skills` result means the server loaded a different config than you intended.

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

## Sources

`directory` scans recursively for `SKILL.md` files (a `FileSystemWatcher` picks up edits live).
`git` shallow-clones via the `git` CLI into `~/.mcpOrchestrator/skills-cache/` and re-fetches
every `pollSeconds` (default 300) — only the latest files, no history, and the cache is a
disposable mirror that is hard-reset on every refresh. `http` fetches a discovery index (the
[Agent Skills discovery format](https://agentskills.io); entries may add a `files` array to
enumerate supporting files — plain HTTP has no directory listing). Invalid skills are logged and
skipped, never fatal; on a name collision the earlier source in config order wins. A skills
section in a [central config](../McpOrchestrator/README.md#central-configuration) works too
(`directory` sources with machine-local placeholders are rejected there), and config edits
hot-reload like everything else.

**Which source type do I want?** `directory` for skills on this machine (simplest — start here).
`git` for a team-shared skills repo: someone merges a skill improvement, and every orchestrator
polling that repo serves it within `pollSeconds` — review and versioning come free via normal
PRs. `http` for published skill sets behind a plain URL. All three end up in the same in-memory
snapshots and are served identically.

## Private git repos — do I need a token?

Only if `git clone <url>` wouldn't already work on that machine:

1. **Public repo** → no token, just the URL.
2. **Your dev machine with stored git credentials** (e.g. Git Credential Manager) → no token; the
   orchestrator shells out to your `git`, so its credentials are used. Test: if `git clone <url>`
   succeeds in a terminal, the same URL works in the config.
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

**Mode B — resources** (`resources`, default on): in practice mode A is what agents actually use —
few MCP hosts surface resources to the model today (in Claude Code, type `@` to attach one
manually) — so treat this mode as forward-compatibility. Each skill file is an MCP Resource at
`skill://<name>/<path>`, plus a `skill://index.json` catalog resource, following
**[SEP-2640](https://github.com/modelcontextprotocol/modelcontextprotocol/pull/2640)**. SEP-2640
is a *pending proposal* still under review; every URI/format convention is isolated in
`Sep2640Conventions.cs` so a spec change is a small diff.

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
