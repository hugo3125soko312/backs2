# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 17: Startup Path Optimization [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 16: MT5 .tkc Cache Extraction [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 15: MT5 Tick Extraction, Verification & Parsing [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 14: MT5 Fetch Optimization & UX [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.15: Time-Domain Monte Carlo [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.14: Selection Queue [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 13: MetaTrader 5 Tick Bridge [COMPLETED]

Sub-phases 13A-13F shipped together. See `docs/ROADMAP_ARCHIVE.md`.

### Phase 13A -- MQL5 bridge script [COMPLETED]
### Phase 13B -- Mt5TickServer + Mt5TickService [COMPLETED]
### Phase 13C -- Config + DI wiring [COMPLETED]
### Phase 13D -- MT5 Ticks tab [COMPLETED]
### Phase 13E -- Mt5TickWriter (CSV) [COMPLETED]
### Phase 13F -- Deployment guide [COMPLETED]

---

## Phase 10.11C2: Equity Curve Rendering [PENDING]
- [ ] Extend Monte Carlo result to store per-simulation equity curves.
- [ ] Spaghetti plot on the Monte Carlo tab.

## Phase 10.11C3: Monte Carlo Toggle Performance [PENDING]
- [ ] Toggling between Histogram and Equity Curves re-renders from scratch every time. With ~1000 curves the switch is visibly slow.
- [ ] Investigate: cache the rendered plot for each mode per selected dataset, invalidate only on new run or dataset switch.
- [ ] Alternatively, keep two `AvaPlot` controls and toggle visibility instead of re-rendering.

---

## Phase 10.7: Chart Tab [IN PROGRESS]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.7C -- Timeframe Switching [PENDING]
### Phase 10.7D -- Crosshair & Readout [PENDING]
### Phase 10.7E -- Polish [IN PROGRESS]

---

## Phase 11: Data Automation [IN PROGRESS]
Sub-phases 11A-11E are in `docs/ROADMAP_ARCHIVE.md`.

### Phase 11F -- Automatic start-position detection [PENDING]
Replace the manual "Calibrate Click" step with a visual pass that finds the latest candle on the chart and starts the arrow iteration from there.

- [ ] Capture a full-window frame after focus + maximize.
- [ ] Locate the price/time axis and the chart plot area within the frame (identify the pixel column where the newest bar sits — typically the right-most non-axis content column).
- [ ] Derive the click point automatically: candle column × vertical center of the plot area.
- [ ] Drop the 200× right-arrow "jump to latest" prelude — the chart is already at the newest bar.
- [ ] Keep "Calibrate Click" as an optional override; auto-detection is the default path.
- [ ] Log the detected start position so a failed detection is diagnosable from the Logs tab.
- [ ] Fallback: if detection fails, fall back to the saved click point and the existing right-arrow prelude.

---

## Phase 18: Data Tab Enhancements [PLANNED]

### Phase 18A -- Total Range column
Add a `Total Range` column to the Data tab grid showing the span covered by each cached dataset (earliest bar timestamp → latest bar timestamp, formatted as `Nd Nh` / `Nmo Nd` / `Ny Nmo` depending on magnitude).

- [ ] Expose `MinT` / `MaxT` on `DataEntry` (`src/core/DataManager.cs`). `CacheManager.ReadCacheMetadata` already collects both in a single pass over `bars[]` (see `docs/ARCHITECTURE.md` §7) — surface them through `DataEntry`.
- [ ] Add `RangeDisplay` to `DataRowViewModel` (`src/gui/Data/DataRowViewModel.cs`) using the same duration-formatting style as `ProviderDatasetRow.FormatDuration` (`src/gui/Providers/ProviderDatasetRow.cs`) so the two tabs are visually consistent.
- [ ] Insert a `Total Range` `DataGridTemplateColumn` in `src/gui/Data/DataView.axaml`. Decide column order — suggested between `Modified` and `Path`.
- [ ] Sort and filter: add `FilterRange` text box in Row 2 and a `RangeSortGlyph`; sort on the raw duration (`MaxT - MinT`, numeric) rather than the display string.
- [ ] Hide on group-header rows via `IsVisible="{Binding !IsGroupHeader}"` like every other column.
- [ ] Sparse-data edge case: a dataset with `MinT <= 0 || MaxT <= 0 || MaxT <= MinT` renders `—` and is excluded from range-based sorting (same rule as `ProviderDatasetRow`).

### Phase 18B -- Data tab first-click latency
The Data tab is the first tab a user interacts with after startup, and its ViewModel is now constructed lazily (Phase 17) — the ~450 ms `AccountService` cost is gone, but `DataViewModel` construction still does a synchronous `RefreshData()` inside its ctor. On a cache with many files, `_dataManager.GetAvailableData()` reads and parses metadata for every `*.json` under `data/cache/`, blocking the UI thread on first tab open.

- [ ] Instrument: add `Stopwatch` markers around `DataViewModel` ctor body (TimeframeMap population, `AvailableProviders` population, `RefreshData()`) and around `DataManager.GetAvailableData`. Log via `LogManager.Info(LogTag.Data, ...)` the way `App.axaml.cs` does for startup. Baseline before optimising.
- [ ] Profile: determine how much of `GetAvailableData` time is `Directory.EnumerateFiles` vs. per-file `ReadCacheMetadata`. The metadata pass is single-pass over `bars[]` (already good) — the question is whether N files × parse dominates at realistic N.
- [ ] Candidate A (cheap): move `RefreshData()` out of the ctor into an async kick-off after first render. The grid shows empty briefly, then populates — the tab is interactive immediately.
- [ ] Candidate B (mid): parallelise the per-file metadata reads with a bounded `Parallel.ForEach`. Files are independent; `ReadCacheMetadata` doesn't hold locks. Should scale linearly with cores.
- [ ] Candidate C (expensive, likely wrong): maintain an in-memory index of cache files, rebuilt only when `data/cache/` changes. Requires a `FileSystemWatcher` or a manual refresh hook — probably overkill for a directory that changes only on Fetch.
- [ ] Same treatment for the post-fetch refresh: `FetchInternal` calls `RefreshData()` synchronously after the fetch completes. Confirm whether that blocks the UI meaningfully when only one file was added — likely a no-op given the cache is small, but worth measuring.
- [ ] Acceptance: tab is interactive within 100 ms of click on a warm cache; grid populates within 500 ms.

---

## Phase 22: Build Performance [PLANNED]

Reduces the dev-loop build time. Baseline (measured 2026-10-07): incremental `dotnet build` = 7.14s, `dotnet build --no-incremental` = 15.56s. Phase 22 addresses the incremental loop via csproj hygiene and protocol changes. The dominant root cause -- the Avalonia 11.0.11 XAML compiler defeating incremental builds -- is tracked separately as Phase 23.

### Phase 22A -- csproj hygiene [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md` (Phase 22A).

### Phase 22B -- Build protocol change
- [ ] Update `docs/AGENT_RULES.md` build protocol. Current text prescribes `dotnet build --no-incremental` for every compile-time check, which forces a 15.56s full rebuild on every iteration.
- [ ] Iteration: `dotnet build` (incremental) or `dotnet run`.
- [ ] Pre-commit verification: `dotnet build --no-incremental -p:RunAnalyzersDuringBuild=true 2>&1 | Select-Object -Last 15`.
- [ ] Same protocol update mirrored in `docs/RULES.md` build-vs-run section.

### Phase 22C -- VS Code workspace settings
- [ ] `.vscode/settings.json` gains `"dotnet.backgroundAnalysis.analyzerDiagnosticsScope": "openFiles"` and `"dotnet.backgroundAnalysis.compilerDiagnosticsScope": "openFiles"`. Reduces editor-side memory and CPU on a solution this size.
- [ ] Update VS Code C# Dev Kit extension to latest — 11.0+ claims up to 39x faster no-change incremental builds and 17x faster for a single changed C# file.
- [ ] Note: this affects editor responsiveness only. `dotnet run` speed is unaffected by these keys.

### Phase 22D -- Measure and close
- [ ] Re-baseline incremental and `--no-incremental` after 22A-B.
- [ ] Acceptance: incremental loop < 4s, `--no-incremental` < 15s. If Phase 23 lands, target moves to < 2s incremental.

---

## Phase 23: Avalonia 11.3 Upgrade [PLANNED]

Root cause of the 7.14s incremental build. Avalonia PR #17539 (merged Nov 2024) reworked `AvaloniaBuildTasks.targets` so the XAML compiler runs as part of `CoreCompile` via `TargetsTriggeredByCompilation`, restoring incremental build correctness. The PR description states the previous behavior "made MSBuild think that the output was always changed between builds, effectively killing incremental builds." That fix shipped in the 11.3.x line; 11.0.11 predates it.

### Phase 23A -- Version bump
- [ ] Bump in lockstep to 11.3.x (latest patch): `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.Controls.ColorPicker`.
- [ ] Verify `ScottPlot.Avalonia 5.0.55` resolves against 11.3.x; check the ScottPlot repo for the compatible Avalonia range before bumping.
- [ ] No other package in `backs2.csproj` has an Avalonia coupling.

### Phase 23B -- Smoke test every touched surface
- [ ] Shell chrome: `ExtendClientAreaToDecorationsHint` + `NoChrome`, fullscreen open, minimize, restore-from-taskbar.
- [ ] DataGrid: Data tab grouping, group-header checkbox bulk select, column sort glyphs, filter row, row-level checkbox.
- [ ] Color picker: `ColorPickerWindow` modal, `ColorSpectrum` / `ColorSlider` / `ColorPreviewer` rendering, hex input, brightness slider to zero, OK / Cancel.
- [ ] ScottPlot: Chart tab candlestick + volume, Monte Carlo histogram / curves / cluster, keyboard pan/zoom handlers.
- [ ] Popup-based controls: menu bar dropdowns, every ComboBox.
- [ ] Theme: accent color change live, reset to defaults, restart persistence.
- [ ] Menu bar: every navigation item routes to the correct tab.
- [ ] Left nav rail: all 12 items select correctly and the accent stripe follows.
- [ ] Cross-tab navigation: Monte Carlo cluster plot click jumps to Chart.

### Phase 23C -- Measure and close
- [ ] Re-baseline incremental after bump. Target: < 2s.
- [ ] If any smoke test fails, revert to 11.0.11 and close the phase as `[DEFERRED]` with the blocking reason recorded.

---

## Phase 24: Tab Responsiveness [IN PROGRESS]

Extends the Phase 21 pattern (off-thread, parallel metadata scan + fire-and-forget ctor refresh) to the remaining tabs whose ViewModels do disk I/O in their constructors. Diagnosis from 2026-10-08: only Data and Accounts are affected. Config, Metrics, Overview, Logs, Chart, Runner, and Monte Carlo do no disk I/O in ctor and are already responsive.

### Phase 24A -- Data tab [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md` (Phase 24A).

### Phase 24B -- Accounts tab [PENDING]
- [ ] `AccountService` ctor scans `data/accounts/` and parses each firm JSON. ~150 ms cold on first Accounts click.
- [ ] Same pattern as 24A: async init + fire-and-forget from `AccountViewModel` ctor.
- [ ] `MonteCarloViewModel` reads `AccountService.Active` at run start — needs a guard for the uninitialised state.
- [ ] Optional: it is under the "noticeable freeze" bar; do only if the phase is otherwise cheap.

### Phase 24C -- Config tab [CLOSED - no change]
Provider discovery via `ActivatorUtilities.CreateInstance` completes in ~8 ms per the Phase 17 startup log. Nothing to fix. Closed for the record.

---

## Phase 12: Hardening & Housekeeping [PLANNED]

---

## Appendix A: Shipped Accounts

Format: ONE JSON per firm at `data/accounts/{Firm}.account.json`.

### FTMO (2 plans)
- **1-Step**: 10K, 25K, 50K, 100K, 200K. Profit Target 10%, Max Daily Loss 3%, Max Loss 10%, Best Day Rule 50%, Profit Split 90%, Trailing EOD drawdown.
- **2-Step**: 10K, 25K, 50K, 100K, 200K. Phase 1 10%, Phase 2 5%, Max Daily Loss 5%, Max Loss 10%, Min 4 trading days, Profit Split 90%, Static drawdown.

### Maventrading (7 plans)
- **Standard 1-Step**: 2K, 5K, 10K, 20K, 50K, 100K. Target 8%, MaxLoss 5%, DailyLoss 3%, Split 80%.
- **Standard 2-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1 8%, P2 5%, MaxLoss 8%, DailyLoss 4%, Split 80%.
- **Standard 3-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1/P2/P3 3% each, MaxLoss 3%, DailyLoss 2%, Split 80%.
- **Instant**: 2K, 5K, 10K, 20K, 50K, 100K. No target, MaxLoss 3%, DailyLoss 2%, Split 80%, Consistency Yes.
- **Mini**: 2K, 5K, 10K, 20K, 50K, 100K. No target, MaxLoss 3%, DailyLoss 2%, Split 70%, Payout 24h, Consistency Yes.
- **Omo 2-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1 6%, P2 8%, MaxLoss 8%, DailyLoss 4%, Split 80%.
- **Buy Now Pay Later**: 2K, 5K, 10K, 20K, 50K, 100K. P1 4%, P1 MaxLoss 10%, Funded MaxLoss 8%, Funded DailyLoss 4%, Split 80%, Funded Consistency 20%.

### Template
- `_TEMPLATE.account.json` — schema reference. Copy it to add a new firm. Rename to `.account.json.txt` to exclude from the scan.