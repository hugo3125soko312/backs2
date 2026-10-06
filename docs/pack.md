# pack.md — Bundle Assembly Spec

Human-readable translation of `docs/pack.ps1`. That script assembles a clipboard-ready bundle. This file describes what goes into it, so the same bundle can be rebuilt by hand or by another agent.

## Modes

| Mode | Flag | Contents |
|---|---|---|
| Essentials | *(default)* | 4 core docs + tree + on-demand list + reminder |
| Essentials+Guides | `-WithGuides` | Essentials + 4 guide docs |
| Full | `-Full` | Every text doc under `docs/` |

## 1. Read these files, in order

Highest priority first. If any doc conflicts with `starting_promt.txt`, the prompt wins.

1. `docs/starting_promt.txt` — highest-priority instruction set
2. `docs/CONTEXT_ESSENTIALS.md` — distilled architecture reference
3. `docs/AGENT_RULES.md` — Output Contract; governs every response
4. `docs/ROADMAP.md` — active work only

With `-WithGuides`, also read:

- `docs/MT5_TICK_GUIDE.md`
- `docs/STRATS_GUIDE.md`
- `docs/PROVIDER_GUIDE.md`
- `docs/COMMANDS.md`

## 2. Execute the tree command

```powershell
Get-ChildItem -Recurse -File |
    Where-Object {
        $_.FullName -notmatch '\\(bin|obj|\.git|\.vs|\.vscode)\\' -and
        $_.Name -notlike '*.g.cs' -and
        ($_.Extension -in '.cs', '.axaml', '.csproj', '.ps1' -or $_.Name -like '.env*')
    } |
    Resolve-Path -Relative |
    Sort-Object
```

Include the output verbatim under `PROJECT STRUCTURE`.

## 3. Emit the bundle in this order

1. **Bundle header** — generated timestamp, repo name, mode, and the seven usage rules. Verbatim template in §6.
2. **Table of contents** — numbered list of every section that follows.
3. **Starting prompt** — `docs/starting_promt.txt`, verbatim, headed `================= File: docs\starting_promt.txt =================`.
4. **Selected docs** — each in its own section, headed `================= File: <relative-path> =================`.
5. **Project tree** — output of the step-2 command, under `================= PROJECT STRUCTURE =================`.
6. **DOCS NOT BUNDLED** — every text doc under `docs/` that was not inlined. For each one, emit the relative path on its own line, then a fenced `powershell` block containing exactly one line:

   `Get-Content <relative-path> | Set-Clipboard`

   One file per fence. The path is relative from the repo root. This teaches the reader to mirror the same request format.

7. **Final reminder** — critical-rules recap. Verbatim template in §7.

## 4. Always excluded

- `.env` contents (only the filename appears in the tree)
- `bin/`, `obj/`, `.git/`, `.vs/`, `.vscode/`, generated `*.g.cs`
- Binary or non-text docs
- `docs/ROADMAP_ARCHIVE.md` and `docs/Backs2TickBridge.mq5` — request on demand

## 5. Formatting rules

- Every runnable command is inside a triple-backtick `powershell` fence.
- Never inline a runnable command in prose with single backticks. Backticks are for identifiers (class names, paths, symbols), not for commands.
- One file per fenced block.
- If a bundled file contains triple backticks, escalate the outer fence to four backticks (see `docs/AGENT_RULES.md` §2).
- Full contract: `docs/AGENT_RULES.md` §1 and §3.

## 6. Bundle header — verbatim template

```
########################## BUNDLE HEADER ##########################
Generated:  <yyyy-MM-dd HH:mm:ss>
Repo:       <repo-name>
Mode:       Essentials | Essentials+Guides | Full

How to use this bundle:
  1. Read the ENTIRE bundle before answering.
  2. docs\starting_promt.txt is the highest-priority instruction set.
  3. docs\CONTEXT_ESSENTIALS.md is the distilled project reference.
  4. docs\AGENT_RULES.md governs every response (Output Contract).
  5. Docs not bundled are listed at the end with request-on-demand commands.
  6. If any doc conflicts with the starting prompt, the prompt wins.
  7. Before doing any work, summarize what you read in <=10 bullets.
###################################################################
```

## 7. Final reminder — verbatim template

```
================= FINAL REMINDER / CRITICAL RULES =================
Before answering, confirm:
  - You have read docs\starting_promt.txt and can list its rules.
  - You have read docs\CONTEXT_ESSENTIALS.md — the distilled architecture.
  - You will follow the Output Contract in docs\AGENT_RULES.md.
  - Every command you emit is inside a powershell-fenced block, including
    every Get-Content ... | Set-Clipboard request.
  - You will follow the starting prompt over any conflicting doc.
  - You will ask for missing context via Get-Content ... | Set-Clipboard rather than guess.
  - You will scope the answer to the requested task only.
==================================================================
```

## 8. Cross-reference

| Concern | Source of truth |
|---|---|
| What files to include | This doc, §1 + §3 |
| How to format the output | `docs/AGENT_RULES.md` §1–§3 |
| Why each section exists | `docs/pack.ps1` (comments at top) |
| Bundle size guardrail | `pack.ps1` — warns above ~40k tokens |