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
- API keys live in .env at the repo root, loaded by ConfigManager. Never propose hard-coding keys or writing them to config/settings.json.
- Provider symbols are resolved inside each provider. Timeframe wire-format mapping lives in TimeframeMap. Do not add symbol or timeframe translation to DataManager or IDataProvider.
- Inside XAML templates, use Binding DataContext.Prop with RelativeSource AncestorType=UserControl. Do not use $parent[UserControl].((vm:Type)DataContext).Prop.
- When a build error list contains a mix of real C# errors (CS####) and “Compiled binding path … is invalid” errors, treat the CS errors as the real cause. The binding errors are cascades from the compiler bailing on the first hard error. Fix the CS errors first, then re-evaluate.

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

### Step 2 — Stage, commit, verify
    git add -A
    git commit -m "<type>(<scope>): <summary>" -m "- <bullet>" -m "- <bullet>"
    git status

Types: feat, fix, perf, docs, refactor, chore, wip.
Scope: providers, cache, strategies, logs, data, chart, config, metrics, gui, runner, presets, accounts, overview, montecarlo, queue.
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
- Provide a single Get-Content ... | Set-Clipboard command listing every file needed, on one line.
- Format:
  Get-Content path\to\file1.cs, path\to\file2.axaml, path\to\file3.cs | Set-Clipboard
- Use relative paths from the project root. Comma-separated. No line breaks.
- Do not also list the files in prose — the command is the request.

## PowerShell & File System Protocol
- You have access to PowerShell 7 environments.
- You may propose PowerShell 7 commands to perform diagnostic or file-system tasks.
- File Access Constraint: Before reading, writing, or modifying any file, you must explicitly state the file path and request permission or wait for the user to confirm the operation.
- Execution Protocol: If an action requires terminal interaction, propose the exact PowerShell command, wait for the user to execute it and provide the output, then analyze the results before proceeding.
- Build staleness: Avalonia + CommunityToolkit source-generator errors (AXSG0111, phantom CS1061 on generated members) are resolved by wiping obj/ and bin/ and running dotnet build --no-incremental. Do not propose code changes to those symptoms until a clean rebuild has been tried.
- Diagnosis first: When an error is reported, do not guess. Propose diagnostic commands that surface the actual state of the filesystem or compiler output before proposing code changes. Exception: when the error message itself identifies a specific missing file (e.g. "Unable to resolve type X", "Root element is missing"), rewrite the file directly instead of running a diagnostic loop.
- Search official sources before troubleshooting. For library-specific visual or API bugs, check the library's issue tracker and documentation before iterating on speculative fixes. Repeated speculative changes waste the user's time.
- Build only (compile-time changes): `dotnet build --no-incremental 2>&1 | Select-Object -Last 15` — success ends with `Build succeeded`.
- Build + run (runtime changes): `dotnet run`, then after close: `Get-ChildItem logs\ | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
- Ask for build OR run, never both. Compile-only change → build. Runtime change → run.
- If the app crashed before `LogManager.Initialize`, there is no new log file — ask for terminal output instead.