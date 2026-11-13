CRITICAL RULES — READ BEFORE DOING ANYTHING:
1. DO NOT read, search, or follow `agents.md`, `project_oop.md`, or anything inside `docs/`. Those markdown files are outdated Avalonia docs and will mislead you.
2. Do NOT edit any `.cs`, `.xaml`, or `.csproj` files in this turn. Your ONLY deliverable in this turn is creating `fixPlan.md` at the repository root.
3. NEVER plan to use `sed` or blind regex replacements. All file edits in future phases must use your file edit tool on exact lines.
4. NEVER plan to write manual/fake `InitializeComponent()` methods in `.xaml.cs`—WPF generates `InitializeComponent()` automatically once `.axaml.cs` duplicates are removed and `x:Class` matches.

STEP 1 — Inspect Current Ground Truth:
1. Run `git status --short` and `git log -n 3 --oneline` to see what is currently modified vs. committed (note commit `8e61c11`).
2. Run `dotnet build -c Release --no-incremental` and read ALL compiler errors verbatim, grouping them by category and file.
3. Search `src/` (ONLY `src/`, NOT `docs/`) for any remaining `*.axaml` or `*.axaml.cs` files.

STEP 2 — Write `fixPlan.md` on Disk:
Create `fixPlan.md` at the repo root with these exact rules at the top of the file, followed by 5 phased checklists (`- [ ]`):

- **Top Rules Section in `fixPlan.md`:**
  * Never read `docs/`, `agents.md`, or `project_oop.md`.
  * Never use `sed` or regex batch edits.
  * Never rewrite or delete ViewModel business logic or partial methods.
  * Always verify with `dotnet build -c Release --no-incremental` after each file/phase.
  * Two-Strike Rule: If `dotnet build -c Release --no-incremental` fails twice on the same file, revert that file (`git checkout -- <file>`), print the raw error, and stop.

- **Phase 1 — Clean Leftover `.axaml*` Files & Verify `Program.cs` / `App.xaml.cs` / `backs2.csproj`:**
  * Keep `<StartupObject>backs2.Program</StartupObject>` in `backs2.csproj` and the fixed `LogsViewModel.cs`.
  * Restore `Program.cs` and `App.xaml.cs` from commit `8e61c11` if uncommitted changes broke their `using` directives.
  * Verify every view has a valid `.xaml` + `.xaml.cs` pair (keeping `WalkForwardTimelineView` as the empty stubbed placeholder) and delete all remaining `.axaml` / `.axaml.cs` files.
  * Delete `obj/` and run `dotnet build -c Release --no-incremental`.

- **Phase 2 — Fix `ChartViewModel.cs` & `MonteCarloViewModel*.cs` Missing Types/Usings:**
  * List the exact files and missing types/usings found in Step 1 (`TimeframeMap`, `NavigateToTradeMessage`, `Dispatcher`, etc.) and resolve them one file at a time.

- **Phase 3 — Fix Remaining Missing WPF `using` Directives Across Views/ViewModels:**
  * List every remaining file missing WPF types (`SolidColorBrush`, `Color`, `Dispatcher`, `StackPanel`, `Orientation`, `HorizontalAlignment`, etc.) and add the exact `using` directives one file at a time until `dotnet build -c Release --no-incremental` reports 0 errors and 0 warnings.

- **Phase 4 — Runtime Launch Smoke Test (`dotnet run -c Release`):**
  * Launch `dotnet run -c Release` and verify startup succeeds without `XamlParseException` or missing resource errors (checking `App.xaml` if any resource key from `Tokens.axaml` is missing).

- **Phase 5 — Git Commit Clean WPF Build:**
  * Stage and commit all verified changes with a clear commit message.

STEP 3 — Mandatory Chat Summary:
Output ONLY this summary in chat after writing `fixPlan.md`:
### Chat Summary
- **Plan Created:** `fixPlan.md` (total phases and total files listed)
- **Current Build Status:** (`dotnet build -c Release --no-incremental` exact error count)
- **Ready for:** Phase 1
