# CLI reference

Everything the `mcp-orchestrator` tool can do, in one place. Three ways to invoke it:

```bash
mcp-orchestrator …                              # if installed globally (dotnet tool install)
dotnet tool execute McpOrchestrator --yes …     # install-free, straight from nuget.org
dnx McpOrchestrator --yes …                     # same thing, .NET 10 shorthand
```

> The `--yes` in the install-free forms belongs to `dotnet tool execute` (it consents to
> downloading the package) — it is **not** an orchestrator flag.

There are three commands. With no command, the tool **runs the MCP server**; `init` and
`profile` are one-shot CLI subcommands that never open an MCP connection to the agent.

| Command | What it does |
| --- | --- |
| `mcp-orchestrator` | Run the MCP server over stdio (this is what your MCP host launches). |
| `mcp-orchestrator init` | Adopt an existing MCP host config into an orchestrator setup. |
| `mcp-orchestrator profile` | Measure the token savings for your setup. Writes nothing. |

---

## `mcp-orchestrator` — run the server

Started by your MCP host (Claude Code, VS, VS Code, Cursor), not by hand. It exposes the three
meta-tools (`list_capabilities`, `discover_tools`, `route`) plus the skills tools
(`list_skills`, `get_skill`, `get_skill_file`) and relays calls to the downstream servers in
its config.

**Option:**

| Flag | Meaning |
| --- | --- |
| `--trace-out <path>` | Append one JSONL line per discover/route interaction, for later replay with `profile --trace`. |

**Environment variables** (set them in the `env` block of the orchestrator's entry in your
host config):

| Variable | Meaning |
| --- | --- |
| `MCP_ORCHESTRATOR_CONFIG` | Absolute path of the orchestrator config file to load. |
| `MCP_ORCHESTRATOR_CONFIG_URL` | Serve the catalog from this HTTPS URL instead of a local file ([central configuration](https://github.com/Byggarepop/dotnet-mcp-orchestrator/blob/main/McpOrchestrator/README.md#central-configuration)). When set, the local path is ignored entirely. |
| `MCP_ORCHESTRATOR_CONFIG_AUTH` | Verbatim `Authorization` header for the central config URL. Set as an OS-level env var, never in a committed file. |
| `MCP_ORCHESTRATOR_CONFIG_POLL_SECONDS` | Central-config poll interval. Default 300, minimum 10. |
| `MCP_ORCHESTRATOR_NO_RELOAD` | `1` disables config hot reload (file watching). |
| `MCP_ORCHESTRATOR_TRACE_OUT` | Same as `--trace-out`. |
| `MCP_ORCHESTRATOR_LOG_DIR` | Directory for the mirrored log file (default `~/.mcpOrchestrator`); `off` disables file logging. |
| `MCP_ORCHESTRATOR_DEBUG` | `launch` or `1` pauses startup until a debugger attaches. Leave unset normally. |
| `MCP_ORCHESTRATOR_AUTOUPDATE` | `1` enables self-update of the Native-AOT binary (applies on the *next* launch). |
| `MCP_ORCHESTRATOR_UPDATE_REPO` | Override the GitHub repo self-update checks (default this one). |

---

## `mcp-orchestrator init` — adopt an existing setup

```bash
mcp-orchestrator init                    # auto-detects a host config in the current directory
mcp-orchestrator init <host-config>      # or point at one explicitly
```

Reads your MCP host config (`.mcp.json`, `.vscode/mcp.json`, `.cursor/mcp.json`, `mcp.json` —
auto-detected in that order), lifts every stdio server into a generated
`orchestrator.config.json` (one capability each, with an auto-generated one-line `summary`),
backs up the host config to `.bak`, and rewrites it to launch only the orchestrator. Remote
(http/sse) servers can't be relayed over stdio and are left untouched.

| Flag | Meaning |
| --- | --- |
| `--out <path>` | Where to write the catalog. Default: `orchestrator.config.json` next to the host config. |
| `--command <cmd>` | What the host should launch for the orchestrator. Default: install-free `dotnet tool execute` pinned to the current version. Pass `mcp-orchestrator` (globally installed tool) or the absolute path to the AOT binary instead. |
| `--dev-feed <path>` | Launch from a local folder feed (the `pack-local.ps1` workflow) so the host always runs your latest local build. Mutually exclusive with `--command`. |
| `--central-url <url>` | Join a team's centrally served catalog: the host config is rewritten so the orchestrator reads its catalog from `<url>`; no local catalog is written and no servers are contacted. HTTPS required (plain http only for localhost). |
| `--print-central` | Print only the generated catalog to stdout and write nothing — for bootstrapping the file a team serves at its central URL. |
| `--no-summarize` | Skip connecting to servers for summaries; keep `TODO` placeholders. Use when a server is slow or side-effectful to start. |
| `--dry-run` | Print both files and the summary; write nothing. |
| `--force` | Overwrite an existing catalog file. |
| `-h`, `--help` | Show the built-in help. |

**Example — joining a team's shared catalog.** Your team serves one orchestrator config from an
HTTPS URL; each developer runs, from the folder with their host config:

```bash
cd ~/my-project
dotnet tool execute McpOrchestrator --yes init --central-url https://config.example.com/orchestrator.central.json
```

This rewrites the host config so the orchestrator reads its catalog from the URL
(`MCP_ORCHESTRATOR_CONFIG_URL` in its `env` block); no local catalog is written and no servers
are contacted. From then on, changes your team pushes to the served file are picked up
automatically within the poll interval (default 300 s) — no restart. If the URL needs
authentication, set `MCP_ORCHESTRATOR_CONFIG_AUTH` as an OS-level environment variable first.

Exit codes: `0` success · `1` usage / IO / parse error.

---

## `mcp-orchestrator profile` — measure before you commit

```bash
mcp-orchestrator profile                          # auto-detects a config in the current directory
mcp-orchestrator profile --host-config .mcp.json  # measure an existing setup; writes NOTHING
mcp-orchestrator profile --trace session.jsonl    # replay a recorded session
```

Two modes: **static** (resting floor, naive baseline, best/worst envelope — deterministic,
CI-friendly) and **trace** (replays a recorded session into the realized per-turn savings
curve). Auto-detect order: `orchestrator.config.json`, `.mcp.json`, `.vscode/mcp.json`,
`.cursor/mcp.json`, `mcp.json`.

| Flag | Meaning |
| --- | --- |
| `--config <path>` | Orchestrator config to profile. |
| `--host-config <path>` | Profile an existing MCP host config instead: its stdio servers are imported in memory and measured; nothing is written. Mutually exclusive with `--config`. |
| `--trace <path>` | Session trace (JSONL, recorded with `--trace-out`) to replay. Selects trace mode. |
| `--format <table\|json>` | Output format. Default `table`; JSON is a superset of the table. |
| `--tokenizer <name>` | Token encoding. Default (and currently only): `cl100k_base`. |
| `--assert-favorable` | (trace) Exit `2` if the orchestrator is *not* favorable for the session — gate a PR on it. |
| `-h`, `--help` | Show the built-in help. |

Exit codes: `0` success · `1` usage / IO / config error · `2` assertion failed
(`--assert-favorable` on a losing session).

---

Both subcommands print this same reference via `--help`; this page exists so it is also
readable without running anything.
