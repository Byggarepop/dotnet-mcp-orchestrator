# Release gotchas (each of these has actually happened)

- **Missing changelog entry.** A feature PR merged without writing `## [Unreleased]` — the
  release notes would have been empty. Convention: every feature/behavior PR adds its entry in
  the same PR; the bump PR only renames the section. If entries are missing at release time,
  reconstruct them from `git log <last-tag>..main` in Keep-a-Changelog style
  (Added/Changed/Fixed/Security) before bumping.
- **Pushing to an already-merged PR's branch.** A commit pushed to a PR branch minutes after
  the PR was squash-merged is stranded — it never reaches `main` and nothing warns you. Check
  `gh pr view <n> --json state` before pushing; if merged, cherry-pick onto a fresh branch off
  `main` instead.
- **`git add -A` in the bump commit.** Swept untracked working files (local settings, draft
  docs) into the release PR. Always stage the two files by name.
- **Branch protection blocks the merge.** `gh pr merge` fails with "base branch policy
  prohibits the merge"; the review requirement can't be satisfied by the PR author. Ask the
  user, then `--admin`.
- **MCP Registry name casing.** The server name `io.github.Byggarepop/dotnet-mcp-orchestrator`
  is case-sensitive (capital B) — it must match the GitHub login that owns the namespace. It
  lives in `.mcp/server.json` AND as the `mcp-name:` HTML comment at the top of the root
  `README.md` (the registry verifies that marker inside the packed nupkg). Never lowercase it,
  never remove the README marker.
- **Root README is the NuGet package page.** It is packed into the nupkg; its links must be
  absolute GitHub URLs or they break on nuget.org.
- **Local Native-AOT verification on Windows** (optional pre-flight): run from PowerShell with
  the VS Installer directory on PATH (`$env:PATH += ";${env:ProgramFiles(x86)}\Microsoft Visual
  Studio\Installer"`), else ilcompiler can't find `vswhere.exe`/`link.exe`. CI's `native-aot`
  workflow is manual-dispatch — it does not gate the release automatically.
- **Tag discipline.** Never tag a commit that isn't on `main`; the tag must be `vX.Y.Z` and
  match the changelog heading `## [X.Y.Z]` exactly, or the release-notes extraction finds
  nothing.
