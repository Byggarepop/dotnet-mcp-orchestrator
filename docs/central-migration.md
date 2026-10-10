# Moving to a central catalog with per-project values

**The contract:** one centrally served catalog is the template, each project's host config
supplies the values, and the orchestrator is the only MCP an IDE needs to know about. This
guide converts an existing setup — a user-level orchestrator entry plus a local catalog that
names every project — into that shape, without breaking the machines that have not moved yet.

Read the model first: [Central configuration](../McpOrchestrator/README.md#central-configuration)
and [Per-project values in a shared catalog](../McpOrchestrator/README.md#per-project-values-in-a-shared-catalog).

## Before you start: version compatibility

The per-project features need the orchestrator version that introduced them (see the
[changelog](../CHANGELOG.md) entry for `enabled` placeholders). Older versions react badly to the
new syntax in a **shared** catalog:

| Syntax | Older orchestrator |
| --- | --- |
| `"enabled": "${USE_X:-false}"` | `enabled` must be a JSON boolean there, so the **whole catalog fails to parse**. A machine with a cached copy keeps the previous catalog; a fresh install starts with no capabilities. |
| `${VAR:-default}` | Not recognised. The placeholder is left as literal text, including the `:-default` part. |
| Unresolved `${VAR}` | Left as literal text with a warning, as before. |

So the order is: **upgrade every machine first, then publish a catalog that uses the new syntax.**
`dotnet tool execute McpOrchestrator --yes` picks up the latest release on next launch; a pinned
`--version` in the host config has to be bumped by hand.

## Step 1 — Inventory what you have

Typical starting point on each machine:

- A user-level orchestrator registration: the Copilot app's **Customize → MCP servers** entry,
  `%USERPROFILE%\.mcp.json` for Visual Studio, the user profile `mcp.json` in VS Code, or a
  user-scope server in Claude Code.
- One local catalog (`MCP_ORCHESTRATOR_CONFIG=C:/Users/you/orchestrator.config.json`) in which
  project-specific settings are expressed as **duplicated entries** — `Unwritten-ProjectA`,
  `Unwritten-ProjectB` — each with its own paths and an instruction telling the agent which
  one to pick.

Keep that catalog; it is the raw material for the template and your rollback.

## Step 2 — Write the template catalog

Start from the local catalog and, for each group of project-suffixed entries:

1. Keep **one** entry and give it the plain name (`unwritten`, not `Unwritten-ProjectA`). The
   agent routes by name, so pick names you are happy to keep.
2. Replace every value that differs per project with a placeholder:
   `"workingDirectory": "${PROJECT_ROOT}"`, `"roots": ["${PROJECT_ROOT}"]`,
   `"env": { "DATABASE_URL": "${DEV_DB_URL}" }`.
3. Delete the "only use this in ProjectA" sentences from `instructions`; there is nothing left
   to disambiguate.
4. For a capability that only some projects need, make it opt-in:
   `"enabled": "${USE_DATABASE:-false}"`. For one that most projects need, opt-out:
   `"enabled": "${USE_BROWSER:-true}"`.
5. Give a sensible default wherever one exists (`${SHARED_LIBS:-C:/Shared/Contracts}`), so a
   project only has to set what is genuinely its own.
6. Remove `${CONFIG_DIR}` and `${SOLUTION_DIR}`; central validation rejects them. Use
   `${ENV_VAR}` or absolute paths.

Write down the resulting list of variable names and what each means. That list is the whole
contract between the catalog and the projects — put it in a `"//variables"` comment at the top
of the catalog so it travels with the file.

Result, abbreviated:

```jsonc
{
  "//variables": "PROJECT_ROOT (required) · DEV_DB_URL (required when USE_DATABASE=true) · USE_DATABASE (default false)",
  "capabilities": [
    {
      "name": "unwritten",
      "summary": "Git-history hole detector for the current repository.",
      "promote": true,
      "command": "dotnet",
      "args": ["tool", "execute", "Unwritten", "--yes", "--", "mcp"],
      "workingDirectory": "${PROJECT_ROOT}",
      "roots": ["${PROJECT_ROOT}"]
    },
    {
      "name": "database",
      "summary": "Read-only SQL access to this project's development database.",
      "enabled": "${USE_DATABASE:-false}",
      "command": "npx",
      "args": ["-y", "@modelcontextprotocol/server-postgres"],
      "env": { "DATABASE_URL": "${DEV_DB_URL}" }
    }
  ]
}
```

### Test the template before publishing it

Central-mode rules (reject `${CONFIG_DIR}`, disable on a missing variable) only apply when the
catalog arrives over HTTP, so test it the same way. Plain `http://` is allowed for localhost:

```powershell
# in the folder holding the template
python -m http.server 8765
```

Then, in a project folder, run the orchestrator with the URL and that project's variables and
call `list_capabilities` from your agent, or just read the log at
`%USERPROFILE%\.mcpOrchestrator\orchestrator.log`. A missing variable shows up as:

```
Capability 'database' is disabled on this machine: ${DEV_DB_URL} not set. A central catalog
resolves ${VAR} from the orchestrator's environment — add the variable to the env block of the
orchestrator entry in your host config (.mcp.json), or set it as an OS environment variable ...
```

## Step 3 — Publish the catalog

Any static HTTPS host works: a raw file URL in a Git repository, blob storage, an internal web
server. If the URL needs authentication, each developer sets `MCP_ORCHESTRATOR_CONFIG_AUTH` as an
OS-level environment variable; never put it in a host config file.

## Step 4 — Point each project at it

In each project folder, let `init` rewrite (or create) the host config:

```bash
dotnet tool execute McpOrchestrator --yes init --central-url https://devops.example.com/mcp/orchestrator.config.json
```

It writes the orchestrator entry with `MCP_ORCHESTRATOR_CONFIG_URL` in its env block and lifts
any stdio servers it finds out of the host config (the catalog is assumed to cover them).
**`init` writes only the URL**; add the project's variables to the same env block by hand:

```jsonc
// C:\ProjectA\.mcp.json
{
  "mcpServers": {
    "orchestrator": {
      "type": "stdio",
      "command": "dotnet",
      "args": ["tool", "execute", "McpOrchestrator", "--yes"],
      "env": {
        "MCP_ORCHESTRATOR_CONFIG_URL": "https://devops.example.com/mcp/orchestrator.config.json",
        "PROJECT_ROOT": "C:/ProjectA",
        "USE_DATABASE": "true",
        "DEV_DB_URL": "postgresql://dev@localhost:5432/projecta"
      }
    }
  }
}
```

Per-host notes:

- **GitHub Copilot app and Copilot CLI** read `.mcp.json` with the `mcpServers` key (a
  `servers` key is ignored). Repo-level servers load only after you accept the folder-trust
  prompt once; the app and the CLI share that trust.
- **VS Code** reads `.mcp.json` with `mcpServers`, or `.vscode/mcp.json` with `servers`.
- **Visual Studio** reads `.mcp.json` with the `servers` key, or `.vs\mcp.json` for a
  per-user, uncommitted copy. `${workspaceFolder}` is not expanded there; use absolute paths.
- **Claude Code** reads `.mcp.json` with `mcpServers`.

If one file must serve both key styles, keep the `servers` copy in `.vs\mcp.json` and the
`mcpServers` copy in `.mcp.json`.

Commit only values that hold on every developer's machine. A path that differs per developer
belongs in an OS-level environment variable that the catalog references directly.

## Step 5 — Remove the user-level registration

Once a project's own `.mcp.json` launches the orchestrator, delete the global one, otherwise
every session gets two orchestrators with overlapping tool names:

- Copilot app: **Customize → MCP servers**, remove the orchestrator.
- Visual Studio: remove the entry from `%USERPROFILE%\.mcp.json`.
- VS Code: remove it from the user-profile `mcp.json`.
- Claude Code: `claude mcp remove orchestrator -s user`.

Keep the global entry only if you also work in folders that have no `.mcp.json` — and then
point it at a separate, small local catalog, not at the parameterized template.

## Step 6 — Verify

Open a project and ask the agent for `list_capabilities`. You should see one entry per
capability, named as in the template, and only the ones that project enables. The log at
`%USERPROFILE%\.mcpOrchestrator\orchestrator.log` confirms the source:

```
Central config mode: serving the catalog from https://... (MCP_ORCHESTRATOR_CONFIG_URL is set).
```

and names any capability disabled for a missing variable. Fix the env block, save, and the next
poll (default 300 s, or restart the host) re-enables it.

## Rollback

Each project's `.mcp.json` is the switch. Replace `MCP_ORCHESTRATOR_CONFIG_URL` with
`MCP_ORCHESTRATOR_CONFIG` pointing at the old local catalog and the orchestrator is back in
local-file mode with the old entries. Nothing else was changed on the machine.
