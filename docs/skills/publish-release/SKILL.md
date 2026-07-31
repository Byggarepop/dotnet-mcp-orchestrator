---
name: publish-release
description: Releases a new McpOrchestrator version end to end — version bump PR, tag, and the automated deploy to GitHub Releases, NuGet, and the MCP Registry. Use when asked to release, publish, deploy, ship, or bump the version of McpOrchestrator.
---

# Publishing a McpOrchestrator release

The release is tag-driven: a `vX.Y.Z` tag on `main` triggers `release.yml`, which builds and
tests on three OSes, produces Native-AOT binaries, creates the GitHub Release, publishes to
NuGet (trusted publishing), and updates the MCP Registry listing. Your job is only to get the
version bump onto `main` and push the tag.

## Preconditions — check before touching anything

1. `CHANGELOG.md` has a `## [Unreleased]` section that actually describes the changes being
   released. Feature PRs are supposed to write their entries there; if it is missing or empty,
   STOP and write it first (from `git log <last-tag>..main`) — the release notes are extracted
   from this section, matched to the tag.
2. `main` is green (the merged PRs passed CI) and your checkout is clean.
3. Pick the version by semver against the `[Unreleased]` content: breaking → major,
   feature → minor, fixes only → patch.

## Step 1 — the bump PR (exactly two files)

```bash
git checkout main && git pull
git checkout -b bump-X.Y.Z
```

- `McpOrchestrator/McpOrchestrator.csproj`: `<Version>` → `X.Y.Z`.
- `CHANGELOG.md`: rename `## [Unreleased]` → `## [X.Y.Z] - <today's date>`.
- Nothing else. Do NOT use `git add -A` (it sweeps untracked local files in); add the two
  files by name. Commit message: `Bump version to X.Y.Z` — no setup instructions, no
  `Claude-Session:` trailer (`Co-Authored-By:` is fine).

Open the PR and squash-merge it. Branch protection requires a review, so the merge needs the
admin override — get the user's explicit go-ahead, then:

```bash
gh pr merge <n> --squash --delete-branch --admin
```

## Step 2 — tag and deploy

Tag only a commit that is on `main`, never a branch:

```bash
git checkout main && git pull    # must now contain the bump commit
git tag vX.Y.Z && git push origin vX.Y.Z
```

The tag push starts the release workflow (~8 minutes). Watch it:

```bash
gh run list --limit 1
gh run watch <run-id> --exit-status
```

## Step 3 — verify

```bash
gh release view vX.Y.Z --json name,assets
```

Expect: three platform zips (`win-x64`, `linux-x64`, `osx-arm64`), the `.nupkg`, and
`SHA256SUMS`, with the `[X.Y.Z]` changelog section as the release notes. The `mcp-registry`
job stamps the tag version into `server.json` at publish time — the committed file staying on
an old version is normal, do not "fix" it.

See [references/gotchas.md](references/gotchas.md) before your first release — it lists the
failure modes that have actually happened.
