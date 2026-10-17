# LONG_WORK Block Format [DEPRECATED]

**Status:** This document described the LONG_WORK multi-file emission
format for the Better DeepSeek browser extension. As of 2026-10-17
the extension and `tools/unzipper/watch_downloads.go` are **not active**.
New files are delivered via paste-CLI — see `docs/AGENT_RULES.md`
"File Emission Patterns" section A.

**Do not emit LONG_WORK blocks.** They will land as inert text in the
chat, not as files on disk.

The original content is preserved in git history. To recover:
`git log -- docs/LONGWORK_FORMAT.md`, then
`git show <sha>^:docs/LONGWORK_FORMAT.md`. If the extension is ever
re-enabled, revert this tombstone commit.