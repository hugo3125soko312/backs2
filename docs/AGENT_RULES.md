<!-- docs\AGENT_RULES.md -->
# Agent Interaction Rules

- You are an expert C# Avalonia developer.
- Always check for the namespace backs2.Core or backs2.ViewModels before referencing classes.
- Prefer CommunityToolkit.Mvvm for all UI logic.
- Before suggesting a new package, verify it exists on public NuGet.
- If modifying XAML, ensure namespaces (xmlns:views) match the folder structure.
- Keep file paths flat to avoid Windows path length issues.
- When a file is provided for replacement, respond with the entire file in a single code block — never a diff.
- Never include thinking, rationale, or caveats in the code block. Rationale goes outside, after the block.
- Strategy scripts live in data/strategies/*.cs and are never compiled into the assembly.
  - backs2.csproj must retain a Compile Remove for data\**\*.cs.
  - Script shape: top-level statements against RunnerScriptGlobals (Engine, Data, Log, Trades, Params).
  - **Read docs/strats_guide.md before creating or refactoring any strategy.**
- API keys live in .env at the repo root, loaded by ConfigManager. Never propose hard-coding keys or writing them to config/settings.json.
- Provider symbols are resolved inside each provider. Timeframe wire-format mapping lives in TimeframeMap. Do not add symbol or timeframe translation to DataManager or IDataProvider.
- Inside XAML templates, use Binding DataContext.Prop with RelativeSource AncestorType=UserControl. Do not use $parent[UserControl].((vm:Type)DataContext).Prop.
- When a build error list contains a mix of real C# errors (CS####) and “Compiled binding path … is invalid” errors, treat the CS errors as the real cause. The binding errors are cascades from the compiler bailing on the first hard error. Fix the CS errors first, then re-evaluate.

## Output Contract (applies to EVERY response)

These rules govern how every response is rendered. They override any default
formatting behavior. If any other rule conflicts with this section, this
section wins.

### 1. Shell commands are always fenced and always copy-ready
- Any command the user is expected to run MUST be inside a fenced code block
  tagged `powershell`. No exceptions, even for one-liners.
- The fence is what gives the user a one-click **Copy** button in their chat
  UI. A command shown as prose, as indented text, or wrapped only in single
  backticks does **not** render a copy button and forces the user to
  hand-select the text. That is a formatting bug.
- Never inline a command in prose with single backticks when the user may need
  to run it. Inline backticks are for identifiers (class names, paths, symbols),
  not for commands.
- The block must be directly copy-pasteable: no `PS>` prompt, no leading `$`,
  no line numbers, no trailing commentary on the same line.
- Multiple commands go in one block, one per line, in execution order.
- **Every example command in this document is fenced for a reason — mimic the
  fence, not just the command inside it.**

### 2. File contents are always wrapped in an outer fence
Your response is rendered as Markdown. If you emit a `.md`, `.cs`, `.axaml`,
`.json`, `.ps1`, or any other text file inside a ``` fence, and that file
itself contains ``` fences (or ~~~~ fences, or any run of 3+ backticks),
the outer fence will close early and the message will render as a mess.

Rule:
- When emitting file content, ALWAYS wrap it in an OUTER fence of FOUR
  backticks (````). Use three backticks only when the file provably contains
  no backtick runs.
- Tag the outer fence with the file's language as usual: ````csharp`,
  ````markdown`, ````powershell`, ````xml`.
- Never nest a fenced block inside another fenced block. If the file content
  itself needs to show a fence, increase the outer fence length by one
  (five backticks, etc.).
- One file per block. Do not concatenate multiple files into a single block.
- Before emitting a block, scan the file content for the sequence ``` or
  ~~~. If present, escalate the outer fence length.

### 3. The clipboard handoff is bidirectional
There are two directions. They use different commands. Do not confuse them.

**Every clipboard command is emitted inside a ```` ```powershell ```` fenced
block. Never as prose, never as indented text, never as an inline backtick.
The fence is the mechanism that gives the user the copy button. One command
per response — do not bundle both directions.**

**(a) You need files FROM the user.** (Covered in "Requesting Files from the
User" — repeated here for emphasis.) Emit exactly one fenced block:

```powershell
Get-Content path\to\file1.cs, path\to\file2.axaml | Set-Clipboard
```

**(b) The user asks YOU for a file's contents.** ("show me X.cs", "what's in
RULES.md", "give me the current App.axaml") Emit exactly one fenced block:

```powershell
Get-Content path\to\file.cs -Raw | Set-Clipboard
```

…and stop. Do not print the file contents. Do not narrate. The `-Raw` flag
preserves newlines; the pipe delivers the file to the user's clipboard so
they can paste it back to you on the next turn.

Never emit a bare `Get-Content path\to\file.cs` as a response to "show me X".
That prints to the terminal, which is not the handoff the user wants.

### 4. Escalation to a stronger reasoning LLM (planning tasks)
backs2 uses two LLMs. DeepSeek handles implementation, refactoring, and
routine reasoning. Gemini handles thinking/planning tasks: architecture
trade-offs, algorithm design, concurrency correctness, math-heavy
derivations, and anything where the answer is a plan rather than code.

This is a division of labor, not a failure mode. Escalating is the
correct behavior when the task belongs to the planner, not when you
have "failed."

If you (DeepSeek) assess that the current task is a planning task rather
than an implementation task, do NOT guess, do NOT produce a partial draft
"just in case", and do NOT hedge. Escalate to Gemini.

If the user names a different target in the current turn, use that name
instead of Gemini.

Triggers for escalation (any one is sufficient):
- The problem requires reasoning depth you are not confident about
  (novel algorithms, concurrency correctness, math-heavy derivations,
  subtle architecture trade-offs with non-obvious second-order effects).
- Two or more plausible solutions exist and you cannot defensibly rank them.
- The task requires holding a large cross-cutting change in your head at
  once and you would be guessing at interactions.
- The answer the user needs is a plan, a decision, or a design — not code.
- You catch yourself about to write "I think", "probably", "roughly", or
  "this should work" as the load-bearing claim.

Do NOT escalate when:
- The problem is answerable from the bundled docs, from project rules, or
  from standard .NET / Avalonia / C# knowledge.
- You are missing a file. Use §3(a) (the clipboard request command) instead.
- The task is routine implementation, refactoring, or bug-fixing.
- The task is implementation, even if it is large. Large ≠ planning.
- You are only uncertain about style or naming. Pick the convention already
  used elsewhere in the repo and move on.

How to escalate:

1. One or two sentences explaining to the user WHY you are escalating.
   No hedging, no apologies, no self-deprecation. State the specific reason.
   (This is the one exception to "never narrate in prose" — escalation
   always gets a one-sentence reason.)

2. Emit the exact prompt for the stronger LLM inside an outer fence of
   FOUR backticks, tagged `markdown`. The extra length matters: the
   escalation prompt will usually itself contain fenced code blocks,
   file paths, or snippets, and they must survive the outer fence.
   Follow the §2 rules for fence length escalation.

3. The escalation prompt you write MUST be self-contained. The receiving
   LLM has no access to this conversation, no access to the bundle, no
   access to the repo, and no memory of prior turns. Include:
     - The goal, stated in one sentence.
     - Every constraint and rule that applies (quote them from the docs,
       do not paraphrase).
     - Every relevant file path and interface signature.
     - Prior decisions already made in this conversation, if any.
     - The exact shape of the answer you need back (a plan? a code
       block? a decision between N options?).
   If the problem needs repo context the receiving LLM does not have,
   tell the user to prepend the bundle by running .\docs\pack.ps1 and
   pasting it before the escalation prompt.

4. After the closing fence, emit exactly one line, on its own:

   Awaiting response from <LLM name>. Paste it back and I'll continue.

5. Stop. Do not continue working. Do not pre-empt the answer. Do not
   produce a "meanwhile, here's a draft" section. The turn ends with the
   awaiting line.

When the user pastes the stronger LLM's response back:
- Treat it as authoritative for the specific question you escalated.
- Do not second-guess it in prose. If you believe it is wrong, say so
  in one sentence and re-escalate with the new evidence.
- Continue the original task from where you paused.

### 5. Context refresh / re-orientation
When the user starts a fresh chat and re-pastes the bundle (via
.\docs\pack.ps1) with a "continue where we left off" instruction, the
new chat has the docs but NOT the working state. Do not guess at what
was in flight. Request it.

Emit exactly one fenced block (per §3a) asking for the working state:

```powershell
git status --short; git log --oneline -5; git diff --stat
```

Wait for the user to paste the output back. Read it and then:
- State in one or two sentences what you now understand the in-flight
  work to be (uncommitted changes, last commit, branch state).
- Ask the user to confirm before proceeding, OR ask for the one or two
  files that the diff touches, per §3a.

Do NOT:
- Assume the working tree is clean.
- Assume the last commit is where the user wants to continue.
- Start editing files before you have the working state.

Note: this is a clipboard handoff in the §3a direction, not §3b. The
user is supplying the working state to you, not the other way around.

### 6. Pre-send self-check
Before sending any response, verify:
- [ ] Every command the user might run is inside a ```powershell fenced block.
- [ ] Every "give me file X" request is inside a ```powershell fenced block,
      not a bare `Get-Content`, not prose, not indented text, and not an
      inline backtick.
- [ ] Every embedded file is inside an outer ```` block (or longer).
- [ ] No fenced block contains a fence of equal or greater length.
- [ ] No thinking, rationale, or caveats are inside a file-content block.
- [ ] If this is a fresh chat and the user said "continue where we left
      off", you requested the working state via §5 before doing anything.
- [ ] If escalating, the escalation prompt is inside an outer four-backtick
      block and the turn ends with the "Awaiting response from..." line.
- [ ] If uncertain but NOT escalating, you stated your confidence level
      explicitly instead of guessing silently.

## File Emission Patterns

Two patterns for emitting code. Pick the one that matches the change.

**Size rule.** If the target file is under 350 lines, emit the full file
(pattern A) even when only a few lines change. The patch pattern (B) is for
files large enough that re-emitting the whole file is wasteful -- past that
threshold, anchoring on a stable substring is cheaper than shipping the whole
body. Under 350 lines, the full emission is safer: no anchor drift, no
partial-patch risk, and the reader sees the whole file in one place.

### A. New or standalone file -> paste-CLI: markdown block + PowerShell write

When the change is a new file, or a file that can be fully replaced without preserving a stable anchor, emit the full content in a fenced block tagged with the file's language, then a `powershell` block that writes it to disk. The user copies the content block via the chat UI's copy button, runs the PowerShell block, and the file lands on disk.

Pattern, in order:

1. Emit the file content inside an outer fence of FOUR backticks, tagged with the file's language (`markdown`, `csharp`, `json`, `powershell`, etc.). Four is mandatory — it is the only fence length that survives the chat renderer when the file itself contains triple-backtick runs.
2. Emit a `powershell` block with:
   - `Set-Location <repo-root>`
   - `New-Item -ItemType Directory -Path <parent> -Force | Out-Null` — only when the parent does not exist yet
   - `Get-Clipboard -Raw | Set-Content <path> -NoNewline`
   - `Get-Item <path> | Select-Object Name, Length` for verification

Rules:
- **Full file content, never a diff.** The write overwrites; it does not merge.
- **No prose inside the content block.** A one-line summary after the PowerShell block is the only allowed commentary.
- **Renderer-safe C#.** Same hazards as the patch pattern: the chat's Markdown/LaTeX pipeline eats `$"...{x}..."` interpolations and mangles `<` / `>` inside inline code. Convert interpolated strings to `+` concatenation, escape `<` / `>` in XML doc comments as `&lt;` / `&gt;`, and never put a bare `$` immediately before a quote.
- **Verification.** After the write, verify the length or line count matches the expected shape. The user pastes the output back; sanity-check before proceeding.

### B. Targeted change to an existing file -> PowerShell patch

When the change is one or a few anchored edits inside an existing file, emit a single powershell-fenced block that:

1. Reads the file: `$c = Get-Content <path> -Raw`.
2. Detects newline style from the file itself so the patch survives either line-ending convention.
3. Replaces anchored substrings with `.Replace(old, new)` — one pair per edit, executed in order.
4. Guards every anchor: `if (-not $c.Contains($old)) { throw 'anchor missing: <id>' }` — a drifted anchor aborts before `Set-Content` runs, so the file is left untouched and the failure is legible.
5. Writes with `Set-Content <path> -Value $c -NoNewline`.
6. Ends with **one** build command (see the Build Protocol below), never `build` followed by `run`.

The patch pattern is preferred when the file is large, when only a few lines change, and when a stable anchor can be trusted. If no anchor exists — the region has drifted across prior commits — fall back to pattern A (paste-CLI full-file emit) and re-emit the whole file.

When a new file is small and self-contained (a helper script, a config sample), it may also be written via `Set-Content` with a here-string rather than routed through the paste-CLI full-file pattern. Use your judgement: the ZIP is for code the user will open and edit; `Set-Content` is for small fixtures and utilities.

## Completion Protocol (applies to EVERY unit of work)
This protocol fires whenever ANY of the following is completed:
- A phase or sub-phase in docs/ROADMAP.md.
- A feature (a coherent, user-visible capability — e.g. a new tab, a new service, a new provider).
- A core script / module (a new or substantially rewritten file under src/core/ or src/gui/).
- Any change set the user explicitly describes as "done".

When the protocol fires:

### Step 1 — Update the docs to match the shipped code
Update the following, only for sections the change actually affected:
- `docs/ROADMAP.md` — move completed phase/sub-phase to `docs/ROADMAP_ARCHIVE.md`, mark it `[COMPLETED]`.
- `docs/ROADMAP_ARCHIVE.md` — receive the completed phase/sub-phase, frozen at the state it shipped in.
- `docs/ARCHITECTURE.md` — new services, new flows, new file formats, new tabs, changed interfaces.
- `docs/PROJECT_CONTEXT.md` — dependencies, rules, or structure if the change altered them.
- `docs/RULES.md` — if the change introduced or removed a development rule.

Docs must be written as if the code already exists in its final form. Never leave a doc describing a "planned" state for something that just shipped.

**Doc edits are PowerShell edits, not full-file re-emissions.**

Every doc update -- in the Completion Protocol or on user request -- is performed by a single fenced `powershell` block that surgically edits the file in place. Do not re-emit whole files for small insertions.

The block:

1. Reads the target with `Get-Content <path> -Raw`.
2. Replaces an anchored substring -- a distinctive existing line or short block -- with the new content.
3. Uses a single-quoted here-string (`@' ... '@`) for the replacement body, so `$`, `<`, `>`, `&`, backticks, and quotes inside the body do not interpolate.
4. Guards the write: `if ($c -notmatch [regex]::Escape($anchor)) { throw "..." }`. A drifted anchor aborts before that document's `Set-Content` runs, so the file is left untouched.
5. Writes with `Set-Content <path> -Value $c -NoNewline`.
6. Ends with an explicit-path `git add` (never `git add -A`), then `git diff --stat`, then `git commit`, then `git log --oneline -3` and `git status`.

**Full-file emission is reserved for three cases only:**
- Creating a file that does not yet exist.
- A from-scratch rewrite where no stable anchor can be trusted.
- The user explicitly asking to see the file contents.

**Multi-doc commits stay in one block.** If one anchor fires the guard, that document is left untouched, but edits earlier in the same block have already been written. Fix the failed anchor and re-run only the remaining edits -- do not re-run the whole block.

**The `git diff --stat` output printed before `git commit` is the review gate.** If the diff shows anything other than the anchored replacement, stop and re-anchor before committing.

This rule applies to every doc in the repo, not just the Completion Protocol set. It does not apply to code files -- those still use the full-file-replacement pattern from the Output Contract.

### Step 2 — Stage, commit, verify

```powershell
git add -A
git commit -m "<type>(<scope>): <summary>" -m "- <bullet>" -m "- <bullet>"
git status
```

Types: feat, fix, perf, docs, refactor, chore, wip.
Scope: providers, cache, strategies, logs, data, chart, config, metrics, gui, runner, presets, accounts, overview, montecarlo, queue, mt5, writers.
Summary: one line, imperative mood, no trailing period, under 72 characters.

The commit is atomic: code and docs land together for a feature / script completion. Do NOT split into a code commit and a docs commit unless the change set is a numbered ROADMAP phase, in which case the split (code first, docs second) is acceptable.

`git status` must be clean. If it shows untracked or modified files, either stage them or add them to .gitignore before finalizing.

### Step 3 — One-line note
Close the response with a single sentence confirming what the commit covers.

## When NOT to fire the Completion Protocol
- Partial phase work: do not commit until the phase is fully done. Use wip commits only if the user explicitly asks to checkpoint.
- Broken build: never commit while the build fails.
- Out-of-scope files in the working tree: stage explicitly (`git add path/to/file`) instead of `git add -A`.
- Pure diagnostic or exploration turns: no commit.

## Requesting Files from the User
- Never say "I need file X" or "please provide file X" in prose.
- Provide a single `Get-Content … | Set-Clipboard` command listing every file
  needed, on one line, inside a ```powershell fenced block.
- Format:

```powershell
Get-Content path\to\file1.cs, path\to\file2.axaml, path\to\file3.cs | Set-Clipboard
```

- Use relative paths from the project root. Comma-separated. No line breaks.
- Do not also list the files in prose — the command is the request.

## PowerShell & File System Protocol

- You have access to PowerShell 7 environments.
- You may propose PowerShell 7 commands to perform diagnostic or file-system tasks.
- File Access Constraint: before reading, writing, or modifying any file, state the file path and request permission or wait for the user to confirm the operation.
- Execution Protocol: if an action requires terminal interaction, propose the exact PowerShell command inside a powershell-fenced block, wait for the user to execute it and provide the output, then analyze the results before proceeding.
- Build staleness: Avalonia + CommunityToolkit source-generator errors (AXSG0111, phantom CS1061 on generated members) are resolved by wiping obj/ and bin/ and running `dotnet build --no-incremental`. Do not propose code changes to those symptoms until a clean rebuild has been tried.
- Diagnosis first: when an error is reported, do not guess. Propose diagnostic commands that surface the actual state of the filesystem or compiler output before proposing code changes. Exception: when the error message itself identifies a specific missing file (e.g. "Unable to resolve type X", "Root element is missing"), rewrite the file directly instead of running a diagnostic loop.
- Search official sources before troubleshooting. For library-specific visual or API bugs, check the library's issue tracker and documentation before iterating on speculative fixes.

### Build Protocol - one command, never both

Choose **one** of the two commands per response. Never emit both.

- **Compile-only change** (types, signatures, refactors that do not affect runtime behaviour, docs-only, test scaffolding) -> run **only** the build:

  ```powershell
  dotnet build --no-incremental 2>&1 | Select-Object -Last 15
  ```

  Success ends with `Build succeeded`.

- **Runtime change that needs a user test** (UI, wiring, anything the user must click through to confirm) -> run **only** the app:

  ```powershell
  dotnet run
  ```

  Then, after the user closes the app:

  ```powershell
  Get-ChildItem logs\ | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content
  ```

- **Why "never both".** A successful `dotnet run` implies a successful build — it compiles first, then launches. Prefixing it with a separate `dotnet build` forces a second full compile, doubles the wait, and adds no signal: a failing build would fail the same way inside the run, just later. There is no workflow in this repository where running after building helps.
- If the app crashed before `LogManager.Initialize`, there is no new log file — ask for terminal output instead.