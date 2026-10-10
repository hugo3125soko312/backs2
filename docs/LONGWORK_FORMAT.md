# LONG_WORK Block Format

The `<BDS:LONG_WORK>` block is a multi-file emission format used by the Better DeepSeek browser extension. It packages one or more files into a single `.zip` that `tools/unzipper/watch_downloads.go` watches for and extracts into the repository root.

## Syntax

- Opening tag: `<BDS:LONG_WORK>`
- Closing tag: `</BDS:LONG_WORK>`
- File tag: `<BDS:create_file fileName="path/to/file.ext">` ... `</BDS:create_file>`

Each file is a separate `create_file` pair. The `fileName` attribute is mandatory and must be a path relative to the repo root. The LONG_WORK wrapper takes no other content -- its only job is to group the `create_file` entries; the parser discards any raw body inside it.

## Rules

1. Angle brackets are required on every tag.
2. One file per `create_file` tag -- do not concatenate multiple files.
3. Full file content, never a diff.
4. Exact relative paths (e.g. `src/core/Foo.cs`), never absolute.
5. No prose inside the LONG_WORK block.
6. Do not nest LONG_WORK blocks.
7. Every opening tag needs a closing tag.

## Renderer hazards

The chat Markdown renderer auto-links URL-shaped strings inside code blocks. An import line such as `github.com/user/repo` becomes `[github.com/user/repo](https://github.com/user/repo)` and breaks the receiving compiler. Escape or split URL-shaped strings when emitting code through this pipeline.

## Source

Format defined by the Better DeepSeek browser extension parser.