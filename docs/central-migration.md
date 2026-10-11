# Move to a central catalog in 7 steps

Goal: one shared catalog for everyone, one `.mcp.json` per project that only fills in values,
and no project-suffixed duplicates like `Unwritten-ProjectA` / `Unwritten-ProjectB`.

Background, if you want it: [Central configuration](../McpOrchestrator/README.md#central-configuration)
and [Per-project values](../McpOrchestrator/README.md#per-project-values-in-a-shared-catalog).

---

## 1. Upgrade every machine

Older orchestrators cannot read the new syntax (`"enabled": "${…}"`, `${VAR:-default}`), and a
shared catalog that uses it would fail to load there. Upgrade first, publish second.

- `dotnet tool execute McpOrchestrator --yes` picks up the latest release on next launch.
- A pinned `--version` in a host config: bump it by hand.

## 2. Make the template from your current catalog

Open your existing `orchestrator.config.json` and, for each duplicated group:

1. Keep one entry. Name it plainly: `unwritten`, not `Unwritten-ProjectA`.
2. Replace each project-specific value with a placeholder:
   `"workingDirectory": "${PROJECT_ROOT}"`, `"env": { "DATABASE_URL": "${DEV_DB_URL}" }`.
3. Delete the "only use in ProjectA" sentences from `instructions`.
4. Capability not every project needs? Make it opt-in: `"enabled": "${USE_DATABASE:-false}"`.
5. Remove any `${CONFIG_DIR}` / `${SOLUTION_DIR}`; use `${ENV_VAR}` or absolute paths.

Add a comment at the top listing the variable names. That list is the contract with projects.

```jsonc
{
  "//variables": "PROJECT_ROOT (required) · USE_DATABASE (default false) · DEV_DB_URL (when USE_DATABASE=true)",
  "capabilities": [
    {
      "name": "unwritten",
      "summary": "Git-history hole detector for the current repository.",
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

## 3. Try it locally before publishing

Central rules only apply when the catalog comes over HTTP. Localhost is fine:

```powershell
cd <folder with the template>
python -m http.server 8765
```

Point one project at `http://127.0.0.1:8765/orchestrator.config.json` (step 5 shows how),
open it in your IDE, and check `%USERPROFILE%\.mcpOrchestrator\orchestrator.log`.
A missing variable is reported like this, and only that capability is switched off:

```
Capability 'database' is disabled on this machine: ${DEV_DB_URL} not set. ... add the variable
to the env block of the orchestrator entry in your host config (.mcp.json) ...
```

## 4. Publish the template

Put it on any static HTTPS host: a raw file in a Git repo, blob storage, an internal web server.
If the URL needs a token, each developer sets `MCP_ORCHESTRATOR_CONFIG_AUTH` as an OS environment
variable. Never in a file.

## 5. In each project: point `.mcp.json` at it

```bash
cd C:\ProjectA
dotnet tool execute McpOrchestrator --yes init --central-url https://devops.example.com/mcp/orchestrator.config.json
```

`init` writes the URL only. Open the file and add the project's values to the same env block:

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

Repeat for ProjectB with its own values. Commit only values that are the same on every
developer's machine.

Skills follow the same pattern: a `directory` source in the central `skills` section with
`"path": "${PROJECT_SKILLS_DIR}"` serves whatever folder each project points the variable at
(see [Central catalog with per-project skills](skills.md#central-catalog-with-per-project-skills)).

Which key your IDE reads:

| Host | File | Key |
| --- | --- | --- |
| GitHub Copilot app / CLI, Claude Code, VS Code | `.mcp.json` | `mcpServers` |
| Visual Studio | `.mcp.json` or `.vs\mcp.json` | `servers` |

The Copilot app and CLI ask you to trust the folder once before loading it.

## 6. Remove the old user-level orchestrator

Otherwise each session starts two orchestrators.

- Copilot app: **Customize → MCP servers**, remove it.
- Visual Studio: delete the entry in `%USERPROFILE%\.mcp.json`.
- VS Code: delete it from the user-profile `mcp.json`.
- Claude Code: `claude mcp remove orchestrator -s user`.

## 7. Check

Open a project and ask the agent to list capabilities. You should see the plain names from the
template and only the capabilities that project enables. The log confirms the source:

```
Central config mode: serving the catalog from https://... (MCP_ORCHESTRATOR_CONFIG_URL is set).
```

---

**Rollback:** in that project's `.mcp.json`, replace `MCP_ORCHESTRATOR_CONFIG_URL` with
`MCP_ORCHESTRATOR_CONFIG` pointing at your old local catalog. Done.
