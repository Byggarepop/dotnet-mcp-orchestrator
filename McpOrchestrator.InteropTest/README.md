# McpOrchestrator.InteropTest

Manual interop probe: verifies that **Microsoft Agent Framework's MCP skill
discovery** ([announcement](https://devblogs.microsoft.com/agent-framework/discover-agent-skills-from-mcp-servers-in-net/))
can discover and load the skills McpOrchestrator serves.

- **Level 1 — raw MCP:** reads the well-known `skill://index.json` catalog
  resource and every `SKILL.md` it references.
- **Level 2 — Agent Framework:** runs `AgentSkillsProviderBuilder().UseMcpSkills(client)`
  against the same connection (driven directly via `InvokingAsync` with a stub
  agent, so no LLM credentials are needed) and asserts the discovered skill set
  matches the index exactly.

## Run it

```
dotnet build McpOrchestrator -c Release
dotnet run --project McpOrchestrator.InteropTest -c Release
```

Exit code 0 and a final `INTEROP OK` line mean both levels passed. The probe
spawns the Release-built orchestrator over stdio with
`McpOrchestrator/orchestrator.config.sample.json`, so the skills under
`docs/skills/` are what gets discovered.

## Deliberately not in the solution

This project references the experimental `Microsoft.Agents.AI.Mcp` alpha
package and drives Agent Framework internals via reflection where no stable
public API exists yet. CI runs `dotnet test` on `McpOrchestrator.slnx`, so the
project stays out of the solution to keep that dependency out of CI builds —
run it manually when re-verifying interop (e.g. after an SDK or SEP-2640
convention change).
