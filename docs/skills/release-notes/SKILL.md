---
name: release-notes
description: Writes user-facing release notes from a git commit range. Use when asked to draft release notes, a changelog entry, or "what's new" text for a release.
---

# Writing release notes

1. Collect the commits since the last release tag:
   `git log <last-tag>..HEAD --oneline --no-merges`
2. Group the changes by user impact — features, fixes, breaking changes.
   Drop internal-only changes (refactors, CI, test-only commits).
3. Write one line per change in the style described in
   [references/style.md](references/style.md).
4. Lead with breaking changes, if any, under their own heading.

## Edge cases

- No commits since the last tag: say so; do not invent content.
- A commit reverts another commit in the same range: omit both.
