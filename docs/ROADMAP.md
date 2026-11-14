# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

## Pending User Testing [BLOCKING]

Thirteen commits landed between 4327e85 and 45e0eb2 without a runtime check. Every one built clean; none has been exercised. Listed newest-first, with what to click and what would count as a failure.

### 1. Log O(n^2) and flush coalescing -- 45e0eb2

What changed: LogsViewModel.AppendLine did a StringBuilder.ToString() and a Split on the whole buffer for every single line, and OnLogged posted a dispatcher callback per line. Now a bounded Queue with one coalesced flush.

Test: open the Logs tab, run an Optimize pass, watch it live.

Pass: log lines appear promptly and in order, the UI stays responsive during the run, no missing or duplicated lines at the end.

Fail: logs stop updating, lines arrive out of order, the tail is truncated below 2000, or the tab freezes.

Also check: Clear View empties the pane and the next log line reappears. Refresh re-reads the file without duplicating existing lines.

### 2. Cache metadata memoization -- 45e0eb2  [PASSED 2026-10-28]

What changed: ReadCacheMetadata memoized on (path, mtime, length).

Test: open the Data tab, then Providers, then Data again. Then fetch a new symbol and return to Data.

Pass: second and third visits are visibly faster. A newly fetched file appears immediately with correct Volume, Size, Modified and Total Range.

Fail: a stale row persists after a refetch, a new file is missing, or metadata (especially Has Volume and Total Range) disagrees with the actual file.

### 3. Data tab filter debounce -- 45e0eb2  [PASSED 2026-10-28]

What changed: filter handlers debounced 150 ms instead of rebuilding the grid per keystroke.

Pass: typing in any filter box feels smooth and the grid settles within about 200 ms of the last keystroke.

Fail: the grid never updates, updates with a stale filter value, or the final state does not match the text in the box.

Also check: clearing a filter restores all rows. Combining two filters (Symbol plus Range) still intersects correctly.

### 4. GPU batch threshold -- 45e0eb2  [PASSED 2026-10-28]

What changed: GPU Monte Carlo batches below 400k total iterations now skip the device.

Pass: an Optimize run at default settings completes normally, and the log shows no GPU dispatch for the coarse pass.

Fail: Optimize errors out, or coarse MC produces empty or degenerate summaries.

### 5. Optimizer progress throttle -- 7f661e4 (Phase 22E)  [PASSED 2026-10-28]

What changed: Progress replaced with ThrottledProgress at 100 ms.

Test: the same Optimize run.

Pass: progress bar advances smoothly, ETA text updates, status line shows the current phase, and the UI no longer stutters.

Fail: progress bar freezes or jumps backwards, ETA shows garbage, the final state never reaches 100 percent, or the status line sticks on a stale phase.

### 6. Backtest dispatch through IComputeDispatcher -- 709fe31 (Phase 29C)  [PASSED 2026-10-28]

What changed: the backtest phase now dispatches through IComputeDispatcher.DispatchBacktestBatchAsync with a caller-supplied body.

Pass: Optimize produces the same version count, the same trade counts and the same Net PnL as before this commit. The 29A, 30 and 33 instrumentation blocks still print with sane numbers.

Fail: fewer versions than the grid size, zero trades everywhere, or the instrumentation blocks vanish.

### 7. Data tab refresh instrumentation -- 7f661e4 (Phase 18B)  [PASSED 2026-10-28]

What changed: an 18B log line with scan vs total time.

Pass: the line appears in the log on Data tab open with plausible numbers.

Also record: the actual numbers, so the 18B acceptance target (interactive within 100 ms, grid populated within 500 ms on a warm cache) can finally be checked.

### 8. In-group column sorting -- fa63e26  [PASSED 2026-10-28 - header clipping fixed via DataGridSortIconMinWidth=0]

Pass: every non-Symbol column header reorders rows inside each ticker group and the glyph flips asc/desc. Symbol still controls group order.

### 9. Avalonia 12 upgrade -- eb1c666 → current  [PASSED 2026-11-11]

What changed: bumped Avalonia from 11.3 to 12.1.5, fixed breaking API changes:
- `SystemDecorations` → `WindowDecorations` (Avalonia 12 property rename)
- `SetTextAsync` → `SetDataAsync` with `DataTransfer`/`DataTransferItem` (clipboard API change)

Test: full click-through of DataGrid grouping and header checkboxes, colour picker modal,
ScottPlot candlestick and MC plots, every ComboBox popup, live theme change,
every menu item, every left-rail node, and cross-tab cluster-plot navigation.

Pass: all visual elements render correctly, no regression in interactivity.

Fail: any visual regression, popup that does not open, or plot that does not render.

Also check: window chrome (title bar, minimize/maximize) renders correctly with the new API.

---

Test: full click-through of DataGrid grouping and header checkboxes, colour picker modal, ScottPlot candlestick and MC plots, every ComboBox popup, live theme change, every menu item, every left-rail node, and cross-tab cluster-plot navigation.

Fail: any visual regression, popup that does not open, or plot that does not render. Revert to 11.0.11 and record the blocker.
---
## Docs Alignment [PENDING]

Multiple docs still describe the app as Avalonia with `.axaml` files, but the repo is WPF (`UseWPF=true`, `ScottPlot.WPF`, winfx XAML namespaces, `src/gui/RunnerView.xaml` not `.axaml`).

Confirmed discrepancies:
- AGENTS.md / ARCHITECTURE.md reference Avalonia 11.3.22, `RunnerView.axaml`, `Grid.RowDefinitions="Auto,*"` string syntax, DataGrid `TextAlignment`/`AlternationCount`/`TextTrimming` — none of these exist in the WPF codebase.
- docs/PLANS/XAML_*.md and docs/WALKFORWARD_UI_WIRING.md describe a rich `RunnerView.axaml` (MC Settings expander, WF Settings expander, subtabs grid) that was never ported to WPF.
- realissues.md DOC-001/DOC-002 confirm the WPF-vs-Avalonia terminology mix.

Impact: any agent following the docs will look for files/APIs that don't exist and may break the WPF build trying to apply Avalonia fixes.

Needed: rewrite AGENTS.md + ARCHITECTURE.md + XAML_* plan docs to describe the actual WPF shell (`App.xaml` DataTemplates, `MainWindow.xaml` ContentControl bound to `SelectedNode.Content`, tab stubs in `src/gui/*.xaml`). No view code changes are part of this item — the tab views remain stubs until separately scheduled.

## Phase 36: Runtime Performance Monitor [COMPLETED]

A 1 Hz runtime health readout in the status bar, plus a sustained-breach log line. Shipped in four commits: 515819d (sampler), b0c4596 (status bar), c5c47a9 (per-metric colouring, CPU units fix), 19cfb73 (VRAM, NVML), a298b22 (Logs FPS fix).

The sampler lives in src/core/Diagnostics/ and costs one small record per second. Per-frame work is a single Interlocked.Increment in MainViewModel.OnFrame, called from MainWindow.Opened via RequestAnimationFrame. A performance monitor that measurably costs performance is self-defeating, so the 1 Hz tick and the near-zero frame hook are the whole design.

Metrics, and what each one actually means:

- FPS — frames per second via RequestAnimationFrame. Real rendered frames, not a timer.
- RAM — available physical memory via GlobalMemoryStatusEx. Deliberately NOT GC.GetGCMemoryInfo().TotalAvailableMemoryBytes, which reports the GC heap budget and never falls, so a low-RAM threshold against it could never fire.
- CPU — this process, normalised to 0-100 of TOTAL machine capacity by dividing by ProcessorCount. The first cut omitted that division, so a 20-core box reported up to 2000% and the 85% threshold fired at ~4% real utilisation. The metric and the threshold must agree on units and nothing in the type system enforces that.
- VRAM — dedicated video memory used BY THIS PROCESS via IDXGIAdapter3.QueryVideoMemoryInfo on the HighPerformance adapter. Not device-wide: another application holding 6 GB will not show. Right metric for "is backs2 leaking VRAM", wrong metric for "is the card full".
- GPU — device utilisation via NVML. NVIDIA only; null on AMD/Intel with one probe and no retry.

Expected readings, which will otherwise look like bugs:

- GPU 0% and VRAM ~12 MB during an Optimize run is CORRECT. After the Phase 29B.1 OOM fix the coarse pass runs 64-version chunks of 100 iterations, well below the 400k GPU batch threshold, so the optimizer never dispatches to the card. The NVML probe works (verified standalone: 0% idle on an RTX 4060); the workload is simply not there. Do not lower the threshold to make the diagnostic look busier.
- VRAM stays near zero until the compute subsystem creates a D3D device.

Two implementation notes worth keeping:

- Per-metric chip colouring, not per-row. A hot CPU turns only the CPU chip red. That is why the status bar binds an ItemsControl of PerfChip rather than one formatted string with a single boolean.
- Breach handling is debounced: 2 s sustained before a metric counts, 30 s per-metric cooldown on the log line. A single dropped frame must never flash the status bar or write a log line, and an uncooldowned log path would recreate the Logs-tab flood this same phase had to fix.

Verification hooks, all in Program.cs alongside --et-verify and --mc-verify:

- --vram-verify prints the adapter budget. Verified: 7188 MB on an RTX 4060, which matches the card.
- --gpu-verify prints device utilisation. Verified: 0% idle.
- --mc-bench reports dispatch-shape timings used by Phase 29B.1.

Outstanding: the in-app readout has been exercised only by eye, not measured. The Logs FPS fix (a298b22) corrected two bugs — a visibility gate that never fired because tab switching flips IsVisible rather than detaching the view, and a flush that rebuilt a 2,000-line string on every burst — but the resulting frame rate during an Optimize run with Logs open has not been quantified. If it still degrades, instrument the flush itself rather than guessing again.

## Phase 35: exec-bridge Production Hardening [PLANNED]

Redesign of the exec-bridge from a chat-to-shell prototype into a
controlled execution service. Source: internal design review, not
bundled.

Current state: raw `exec.CommandContext` in the daemon process, regex
approval filter, in-memory question/approval queues, single-file JSON
dedup. Adequate for one user on one machine; not for anything else.

The redesign is staged. Each sub-phase leaves the bridge working. No
big-bang rewrite.

Priority order: sandboxed worker -> typed command API -> GUI approval
-> job persistence -> auth/audit -> observability/streaming.

### Phase 35A -- Sandboxed Worker Boundary [PLANNED]

Move command execution out of the daemon process. Even without a full
sandbox, separating the worker bounds the blast radius.

- □ New `exec_bridge_worker.exe`: reads a `JobSpec` JSON on stdin,
  runs the command, writes a `JobResult` JSON on stdout.
- □ Daemon spawns the worker per job (or keeps a small pool),
  dispatches, collects, reaps.
- □ Worker enforces a per-job memory cap (Win32 Job Object on Windows,
  cgroups v2 on Linux) and a CPU/wall timeout.
- □ Worker's filesystem view: only `$Root` and its descendants.
- □ No host network by default; opt-in per job via `JobSpec.AllowNetwork`.
- □ Daemon stops importing `os/exec` entirely; only the worker does.
- □ `-qterm` approvals unchanged; this phase is execution-side only.

### Phase 35B -- Typed Command Registry [PLANNED]

Regex gating becomes defense-in-depth; the primary gate is a typed
command registry.

- □ `CommandKind` enum: `ShellCommand`, `ReadFile`, `WriteFile`,
  `ListDir`, `GitStatus`, `GitDiff`, `GitLog`, `RunTests`, `Build`.
- □ `CommandSpec` struct: kind, args, cwd, targetPaths, timeout,
  requestedBy, idempotencyKey.
- □ Registry maps each kind to an executor. Unknown kinds refused.
- □ New fence tags map to kinds:
  `powershell-read=id` -> `ReadFile`,
  `powershell-write=id` -> `WriteFile`, etc.
  `powershell-E` / `-ER` remain but as `ShellCommand` kind.
- □ File ops use `os` APIs, never a shell. No string interpolation
  into a command line.
- □ Approval policy by kind: `ReadFile` / `ListDir` / `GitStatus` are
  pre-approved; `WriteFile` and `ShellCommand` require approval;
  destructive targets escalate.

### Phase 35C -- Job Model & Persistence [PLANNED]

- □ `Job`: id, kind, spec, status, timestamps, caller, approval state,
  result refs.
- □ Status machine: `queued -> awaiting-approval -> running ->
  completed | failed | cancelled | denied`. Illegal transitions
  rejected.
- □ Persist to `data/jobs/{job_id}.json`. Output to
  `data/jobs/{job_id}.out` and `.err`.
- □ Retention: prune jobs older than N days (default 30, configurable).
- □ History endpoint: `GET /jobs?limit=50&status=failed`.
- □ Retry: rerun a failed job under a new id with a parent reference.
- □ Idempotency: `idempotency-key` header replaces the current
  `(url, id)` dedup. The JSON state file becomes legacy.

### Phase 35D -- Approval UI [PLANNED]

Terminal stays as admin fallback. Add a GUI on the existing HTTP
server.

- □ Static page at `GET /` (localhost-only). Vanilla JS, no build step.
- □ Shows pending approval: normalized command, kind, cwd, target
  paths, risk class, timeout, requested-by.
- □ Buttons: `Allow once`, `Allow for session`, `Block forever`.
- □ Command history view, filterable by status.
- □ Live output tail for running jobs (see 35F).
- □ `-qterm` remains for headless / scripted use.

### Phase 35E -- Structured Risk Classification [PLANNED]

Replace single-tier regex with a classification layer.

- □ `RiskClass` enum: `safe`, `warning`, `destructive`, `critical`.
- □ Shell commands: tokenize, classify by first-word and flag
  combinations -- not blind regex.
- □ File kinds: target inside `$Root` = `warning`; outside =
  `destructive`; `.git/` or mass-delete patterns = `critical`.
- □ Approval UI shows the class with a color and the reason
  ("touches .git/", "outside working directory", "force flag").
- □ `destructive` and `critical` require typing the command back
  (not just Enter).
- □ Regex rules survive as an additional signal, not the sole gate.

### Phase 35F -- Streaming Output & Cancellation [PLANNED]

- □ Worker streams stdout/stderr as length-prefixed JSON frames
  instead of buffering. Daemon relays via Server-Sent Events.
- □ `POST /jobs/{id}/cancel` sends interrupt, then kills after a
  grace period.
- □ Job log persistence writes streaming output to disk live.
- □ Approval UI subscribes to `/jobs/{id}/stream` for live tail.
- □ Remove `strings.Builder` accumulation from the daemon hot path.

### Phase 35G -- Observability [PLANNED]

- □ Structured logs: one JSON line per event with `ts`, `job_id`,
  `kind`, `status`, `dur_ms`, `exit_code`, `risk_class`.
- □ Counters: `jobs_total{kind,status}`, `approvals_total{decision}`,
  `exec_duration_ms`, `approval_latency_ms`.
- □ `GET /metrics` in Prometheus text format.
- □ Optional: `data/metrics.json` snapshot for offline inspection.

### Phase 35H -- Auth & Remote Hardening [DEFERRED]

Needed only if the bridge ever goes multi-user or remote. Loopback-only
today, so deferred until there's a reason.

- □ HMAC-signed request envelope, key from `ConfigManager`.
- □ Optional mTLS for remote callers.
- □ Per-user identity via `X-Bridge-User`, mapped to a policy.
- □ Audit log: every approval and execution with the acting user.

### Out of scope

- Full container / microVM isolation. Phase 35A's OS-level restrictions
  are the stopping point for now.
- Replacing the browser userscript. Tags keep working; changes are
  server-side.

### Deferred to later

- Removing `powershell-E` / `-ER` in favor of only typed kinds.
  Depends on how the model handles the new tags in practice.
- Unifying with the Compute Worker IPC pattern (Phases 27-28). Same
  shape; worth consolidating eventually.

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

- □  
Extend Monte Carlo result to store per-simulation equity curves.
- □  
Spaghetti plot on the Monte Carlo tab.

## Phase 10.11C3: Monte Carlo Toggle Performance [COMPLETED 2026-10-28]

- □  
Toggling between Histogram and Equity Curves re-renders from scratch every time. With ~1000 curves the switch is visibly slow.
- □  
Investigate: cache the rendered plot for each mode per selected dataset, invalidate only on new run or dataset switch.
- □  
Alternatively, keep two `AvaPlot` controls and toggle visibility instead of re-rendering.

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

- □  
Capture a full-window frame after focus + maximize.
- □  
Locate the price/time axis and the chart plot area within the frame (identify the pixel column where the newest bar sits — typically the right-most non-axis content column).
- □  
Derive the click point automatically: candle column × vertical center of the plot area.
- □  
Drop the 200× right-arrow "jump to latest" prelude — the chart is already at the newest bar.
- □  
Keep "Calibrate Click" as an optional override; auto-detection is the default path.
- □  
Log the detected start position so a failed detection is diagnosable from the Logs tab.
- □  
Fallback: if detection fails, fall back to the saved click point and the existing right-arrow prelude.

---

## Phase 18: Data Tab Enhancements [IN PROGRESS]

### Phase 18A -- Total Range column [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 18A).

### Phase 18B -- Data tab first-click latency [COMPLETED 2026-10-28] -- 2026-10-25 (7f661e4): both optimizations this phase proposed were already present (fire-and-forget ctor refresh, Parallel.For inside Task.Run). Added the instrumentation the phase asks for first: a refresh now logs its scan time and total under LogTag.Data. Baseline still unmeasured, so no further optimization is justified until that number exists.

The Data tab is the first tab a user interacts with after startup, and its ViewModel is now constructed lazily (Phase 17) — the ~450 ms `AccountService` cost is gone, but `DataViewModel` construction still does a synchronous `RefreshData()` inside its ctor. On a cache with many files, `_dataManager.GetAvailableData()` reads and parses metadata for every `*.json` under `data/cache/`, blocking the UI thread on first tab open.

- □  
Instrument: add `Stopwatch` markers around `DataViewModel` ctor body (TimeframeMap population, `AvailableProviders` population, `RefreshData()`) and around `DataManager.GetAvailableData`. Log via `LogManager.Info(LogTag.Data, ...)` the way `App.axaml.cs` does for startup. Baseline before optimising.
- □  
Profile: determine how much of `GetAvailableData` time is `Directory.EnumerateFiles` vs. per-file `ReadCacheMetadata`. The metadata pass is single-pass over `bars[]` (already good) — the question is whether N files × parse dominates at realistic N.
- □  
Candidate A (cheap): move `RefreshData()` out of the ctor into an async kick-off after first render. The grid shows empty briefly, then populates — the tab is interactive immediately.
- □  
Candidate B (mid): parallelise the per-file metadata reads with a bounded `Parallel.ForEach`. Files are independent; `ReadCacheMetadata` doesn't hold locks. Should scale linearly with cores.
- □  
Candidate C (expensive, likely wrong): maintain an in-memory index of cache files, rebuilt only when `data/cache/` changes. Requires a `FileSystemWatcher` or a manual refresh hook — probably overkill for a directory that changes only on Fetch.
- □  
Same treatment for the post-fetch refresh: `FetchInternal` calls `RefreshData()` synchronously after the fetch completes. Confirm whether that blocks the UI meaningfully when only one file was added — likely a no-op given the cache is small, but worth measuring.
- □  
Acceptance: tab is interactive within 100 ms of click on a warm cache; grid populates within 500 ms.

---

## Phase 26: Parameter Optimizer & Version Store [PLANNED]

Extends the Runner into a parameter-search tool. The user declares min / max / increment per numeric `@param`; the optimizer generates a bounded Cartesian grid, backtests each version, runs Monte Carlo per version, and surfaces the results in a repurposed Chart tab. Removes the source-preview pane and folds MC settings into the Runner. The Monte Carlo tab, the cluster-plot publisher, and `NavigateToTradeMessage` are KEPT - 26E was cancelled 2026-10-28.

### Design decisions locked (2026-10-08)

- **Search:** bounded Cartesian with a configurable cap. Default cap **500 combinations** (`AppSettings.OptimizerMaxCombinations`). Above cap, the UI offers a random sample of the full grid.
- **Two-pass MC:** **100 iterations** during the search phase (`OptimizerCoarseIterations`), full **1000+ iterations** on the **top 20 survivors** (`OptimizerRefineIterations`, `OptimizerRefineTopN`). All four values configurable.
- **Version storage:** `data/optimizations/batch_{timestamp}.json`, one file per batch. Per version: parameter dict, backtest scalars, MC scalar stats (~30 doubles), `TerminalBalances[]` (1000 doubles), and a **downsampled representative equity curve** — the simulation closest to the median terminal balance, resampled to 1000 evenly-spaced points. ~20 KB per version; ~10 MB per 500-version batch.
- **Execution:** sequential per version. MC saturates cores internally via its own `Parallel.For`; version-level parallelism is not pursued until 26B.1 verification passes.
- **ETA:** calibrated on the first 5 versions via a moving average of backtest and MC times; `gpuAccelerationFactor` parameter (default 1.0) baked into the formula for the future GPU path.
- **Chart tab:** DataGrid of versions (sortable by any column) on the left; selected-version detail on the right (stats + static histogram + representative curve); sparkline strip along the bottom. No interactive plots — static renders only (the interactive ScottPlot path is too laggy per earlier discussion).

### Phase 26A -- Version data model & storage [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 26A).

### Phase 26B -- Optimizer engine

- ☑  
**26B.1 Verification [PASSED]:** ScriptRunner is thread-safe; the StrategyManager throttle can be bypassed by the optimizer. Version-level parallelism is viable. See `docs/ROADMAP_ARCHIVE.md` (Phase 26B.1).
- ☑  
`src/core/Optimizer/ParameterGridBuilder.cs` — Cartesian product from min / max / increment per parameter. Hard cap enforced here; random-sample fallback when over cap.
- ☑  
`src/core/Optimizer/OptimizationRunner.cs` — the per-version loop: backtest → build `TimeSlice[]` → MC at coarse iterations → emit partial result → next version. After the loop completes, refine top-N survivors at full iterations.
- ☑  
Progress reporting: fraction of total work per stage (grid → backtest per version → MC per version → refine pass). Reported via `IProgress<OptimizationProgress>`.
- ☑  
Cancellation: single global `CancellationTokenSource` for the whole run, flowing into every backtest and MC call.

### Phase 26C -- ETA estimator [COMPLETED]

**Parallel-topology fix (2026-10-10).** The optimizer now runs coarse and refine MC under an outer-parallel loop with `outer = min(combos, cores)` and `inner = cores / outer`. The current `EtaEstimator.Estimate` multiplies `remainingCombos` by an average per-version time as if the loop were sequential - every estimate is over-reported by the outer degree (~20x on a 20-core box).

- ☑  
Rewrite `EtaEstimator.Estimate` so each phase's contribution is divided by its outer degree: `(remainingBacktests / W_backtest) * avgBacktest + (remainingCoarse / W_coarse) * avgCoarse + (remainingRefine / W_refine) * avgRefine / gpuFactor`.
- ☑  
Thread phase-specific outer degrees through from `OptimizationRunner` to `EtaEstimator`. Each phase currently computes its own `outer` locally - pass it in.
- ☑  
Expose a single `EstimateTotal()` that returns one wall-time number covering the **whole** run (backtest + coarse MC + refine MC + save) from the moment the grid starts building. The Runner's ETA display should read one number, not stitch per-phase pieces together at call sites.
- ☑  
Normalize the moving-average sample per unit of work (ms per version, ms per simulation) rather than ms per wall-clock, so the average stays valid across topology changes.
- ☑  
`src/core/Optimizer/EtaEstimator.cs` — moving average of backtest and MC times, updated after each version. Blind for the first 5 versions, then extrapolates.
- ☑  
Formula: `ETA = remainingCombos * (AvgBacktestTime + AvgMCTime / gpuAccelerationFactor)`.
- ☑  
UI: `TextBlock` in the Runner, updated every ~5 seconds via a throttled timer to avoid UI thread jitter.
- ☑  
Per-parameter table extension: `RunnerParameterViewModel` gains `Min`, `Max`, `Increment` observable properties, a derived `ValueCount`, and a per-row preview.
- ☑  
`gpuAccelerationFactor` reads from a new `AppSettings.OptimizerGpuFactor` setting (default 1.0). Not exposed in the UI yet; the setting exists so the future GPU path is a value change, not a schema change.

### Phase 26D -- Runner UI rework

- ☑  
**26D.1 -- Ship MC settings first [COMPLETED]:** MC settings panel added to Runner, backed by `RunnerViewModel` fields; active account banner and ruin threshold auto-update on `AccountService.ActiveChanged`. MC tab untouched.
- ☑  
**26D.2a -- Chooser + source viewer [COMPLETED]:** Source-preview column removed. Two-column layout: filter box + `ListBox` chooser + `View Source...` on the left, parameter panel on the right. `StrategySourceWindow` (`src/gui/Strategy/`) opens read-only source on demand. Diff-based `FilteredStrategies` update so filtering does not null the selection.
- ☑  
**26D.2b:** Add min/max/increment fields per numeric `RunnerParameterViewModel`; live combination-count per parameter and per grid; cap warning when total exceeds `OptimizerMaxCombinations`.
- ☑  
**26D.2c:** Add `Optimize` button next to `Run`; wire to `OptimizationRunner.RunAsync`; progress bar + ETA display driven by `OptimizationProgress`.
- ☑  
`RunnerParameterViewModel` gains min / max / increment fields per numeric parameter. Derived `ValueCount` per parameter, `TotalCombinations` across all parameters. UI blocks `Optimize` when `TotalCombinations` exceeds the cap and offers a random-sample mode.
- ☑  
Add an `Optimize` button next to `Run`. `Run` keeps single-configuration behavior. `Optimize` runs the grid.
- ☑  
ETA display and progress bar wired to the optimizer.
- ☑  
Optimizer settings block (cap, coarse iters, refine iters, refine top N) added to the Runner's MC settings panel.

### Phase 26F -- Chart tab repurpose [OBSOLETE - 26E cancelled 2026-10-28]

- □  
`ChartViewModel` drops `Series`, `SelectedDataset`, `SelectedTimeframe`, all crosshair fields, `SetPlotControl`, and the `IRecipient` implementation. No longer consumes `SelectionQueue`.
- □  
New shape: `Batches` (list of optimization batch files on disk), `SelectedBatch`, `Versions` (per-version rows), `SelectedVersion`, `VersionDetail` (stat block + terminal-balance histogram + representative curve).
- □  
Left column: version `DataGrid` — combination index, one column per parameter, PRuin, PFunded, EdgePercent, median terminal balance. Sortable on every column.
- □  
Right pane: selected-version stats (the full 30-field block), static histogram image, static curve image. Renders generated off the UI thread and cached per version.
- □  
Bottom strip: sparkline images, one per version, up to a few hundred. Static; generated lazily.
- □  
Delete `ChartPlotBuilder.cs`, `ChartSeriesFactory.cs`, `ChartAxisFactory.cs` if unused after the rework.
- □  
Remove the keyboard pan/zoom tunnel handler from `ChartView.axaml.cs` (no interactive plots to navigate).

### Phase 26G -- Compute worker migration

- □  
Move `OptimizationRunner` execution into the Compute Worker (`backs2.exe --worker`). Belongs with the eventual Compute Worker Phase 3 migration. Out of scope for 26A-F.

### Deferred -- GPU acceleration

See `docs/ARCHITECTURE.md` "Future: GPU acceleration" note (added with Phase 26's first commit). Summary: CPU-only today via `Parallel.For` inside `MonteCarloEngine.Run`; future path via a compute framework such as `ILGPU` or `ComputeSharp` for the hot `SimulateOnce` loop; keep `MonteCarloEngine.Run`'s public surface stable via an `IComputeBackend` parameter or a CPU/GPU split behind a common interface. `AppSettings.OptimizerGpuFactor` (26C) is the placeholder hook that lets the ETA model project a GPU-enabled run without a schema change.

---

## Phase 27: GPU-Accelerated Monte Carlo [SUPERSEDED]

See Phase 28 and Phase 29 for the unified compute architecture.

Extends the optimizer and MC engine with a GPU compute path. Scope is deliberately narrow: only `MonteCarloEngine.SimulateOnce` is a target. Every other compute surface -- `BuildResult` sort/percentile, `IMetric.Calculate`, `KMeansClusterer`, `TimeSliceBuilder`, per-combination backtests -- is explicitly rejected (see Rejection list below). Rationale: `SimulateOnce` is the only workload with enough scale (100k iterations x ~60k slices) to amortize GPU dispatch overhead.

**Preconditions.** Phase 26 must be fully closed (`AppSettings.OptimizerGpuFactor`, the ETA model, and the optimizer UI all shipped). Compute Worker Phase 3 must be complete -- `MonteCarloEngine` must already run in the separate `backs2.exe --worker` process before this phase begins. The worker holds the GPU. If the parent UI process held the GPU, IPC would need to carry raw simulation state across the 64 MB anonymous-pipe frame cap; worker-holds-GPU keeps the parent-to-worker payload to a small `JobConfig` JSON and the worker-to-parent response to aggregated scalars.

**Framework.** ComputeSharp. DirectX 12 backend -- NVIDIA, AMD, and Intel GPUs without CUDA lock-in. .NET 8 native, source-generator based, no external C++ build step, available on public NuGet. ILGPU was the alternative; ComputeSharp wins on the worker-process constraint (source generators compile kernels into the assembly the worker already loads -- no separate runtime compilation step or shader bundle to ship).

**Interface shape.** `IComputeBackend` injected into `MonteCarloEngine` via constructor injection. Not a CPU/GPU split, not two engine classes -- one interface with one method, two implementations behind it. Fallback to CPU is a runtime decision (`GpuComputeBackend` probes for a device; on failure it delegates to a `CpuComputeBackend` instance). Sketch:

public interface IComputeBackend
{
Task<double[]> RunSimulationsAsync(
TimeSlice[] slices,
int targetDurationSlices,
int iterations,
CancellationToken ct);
}

Boundary is `TimeSlice[]` in, `double[]` terminal balances out. `BuildResult` (sort, percentile, streak distributions) stays on the CPU. `CancellationToken` is checked between kernel dispatches; large iteration counts are chunked to avoid Windows TDR (Timeout Detection and Recovery).

> **STATUS (2026-10-28):** Phase 27A/B/C were superseded by Phase 28 (Unified Computing Subsystem) and Phase 29 (Compute Retrofit), which shipped the same design under different names. The open boxes below are historical, not pending. Closed.

### Phase 27A -- Backend abstraction & CPU parity

- **Size:** S · **Depends on:** Phase 26 closed; Compute Worker Phase 3 complete · **Succeeds when:** `MonteCarloEngine` runs through `IComputeBackend` with no measurable regression on the CPU path.
- □  
Extract `IComputeBackend` interface with the `RunSimulationsAsync` signature above.
- □  
Implement `CpuComputeBackend` wrapping the existing `Parallel.For` + `SimulateOnce` logic.
- □  
Register `IComputeBackend` in `App.axaml.cs` -> `ConfigureServices` (singleton, `CpuComputeBackend` initially).
- □  
Update `MonteCarloEngine` constructor to accept `IComputeBackend` via DI.
- □  
Verify every existing caller (`OptimizationRunner`, `MonteCarloViewModel`, any test harness) still resolves the engine through DI with no caller-side change.
- □  
Baseline: CPU wall time for a known grid unchanged within +/- 5%.

### Phase 27B -- GPU kernel implementation

- **Size:** L · **Depends on:** 27A · **Succeeds when:** `GpuComputeBackend` produces statistically identical results to `CpuComputeBackend` on the same input (P(ruin), P(funded), terminal-balance percentiles match to 3 sig figs at 10k iterations).
- □  
Add `ComputeSharp` PackageReference to `backs2.csproj`.
- □  
Implement `GpuComputeBackend` in `src/core/Overview/GpuComputeBackend.cs`.
- □  
Port `SimulateOnce` to a `[Shader]`-attributed compute kernel reproducing the exact state machine: MTM-low vs daily-floor / max-DD floor check, then realized PnL, then HWM update, then EOD reset.
- □  
Per-iteration RNG must be seed-split the same way the CPU path does it (`new Random(seed + i)`) so determinism is preserved when `seed` is supplied.
- □  
Chunk iterations under TDR limits: default batch of ~10k iterations per dispatch, configurable via `JobConfig`.
- □  
Respect `MaxIterations` (100,000) and `MaxCurveCells` (100,000,000) at the backend boundary -- the ceilings apply regardless of backend.
- □  
Fallback: `GpuComputeBackend` constructor probes for a working device; on failure it stores a `CpuComputeBackend` fallback and logs a Warn. `RunSimulationsAsync` delegates rather than throwing.
- □  
Invariants baked into the kernel: Monte Carlo operates in the time domain only. Never percentage-compound. Never scramble trades.

### Phase 27C -- Worker IPC integration

- **Size:** M · **Depends on:** 27B · **Succeeds when:** the Avalonia UI parent can request a GPU-accelerated MC run and receive aggregated stats back; the worker holds the GPU; the parent-to-worker payload stays under the 64 MB `WorkerIpc` frame cap.
- □  
Extend `WorkerEnvelope` with a `bool RequiresGpu` field.
- □  
Extend `JobConfig` with `int GpuDeviceIndex` (default -1 = any available) and `int GpuIterationBatch` (default 10,000).
- □  
Update `WorkerHost` to resolve `IComputeBackend` from the job config: `RequiresGpu = true` -> try `GpuComputeBackend`, fall back to `CpuComputeBackend` on any initialization failure.
- □  
Worker logs the chosen backend and (on GPU) the adapter name at Debug level.
- □  
Parent (`ComputeWorkerManager`) surfaces the backend choice in the progress stream so the UI status line can show "GPU: <adapter>" or "CPU (GPU unavailable)".
- □  
Do NOT ship raw per-iteration data across IPC. The worker runs the full MC internally and returns aggregated `MonteCarloResult` scalars plus optional `TerminalBalances[]` (100k doubles = 800 KB, well under the cap).

### Rejection list

Explicitly out of scope for Phase 27. A future contributor should not add a GPU variant of any of these without a new ROADMAP entry that argues the workload shape:

- **`MonteCarloEngine.BuildResult`** -- sort and percentile on 100k arrays. CPU radix sort handles this in milliseconds; GPU round-trip is pure overhead.
- **`IMetric.Calculate`** (VWAP, CVD, RSI, ATR, SessionHigh, ZScore, VP-POC/VAH/VAL) -- sequential time-series dependency chains (EMA in RSI/ATR especially). GPU cores would starve.
- **`KMeansClusterer.Cluster`** -- N = 20-500 trades, K = 3-8 clusters. Dispatch overhead exceeds compute time. Also constrained to seed 42; determinism across GPU wavefronts is not worth the risk.
- **`TimeSliceBuilder.Build`** -- allocation-free, few thousand items, memory-bound not compute-bound. GPU marshalling defeats the allocation-free design.
- **`OptimizationRunner` per-combination backtests** -- would require rewriting strategies as stateless vectorized kernels; a full paradigm shift, not an optimization. Only worth revisiting if parameter grids exceed ~100k combinations.

## Phase 22: Build Performance [PLANNED]

Reduces the dev-loop build time. Baseline (measured 2026-10-07): incremental `dotnet build` = 7.14s, `dotnet build --no-incremental` = 15.56s. Phase 22 addresses the incremental loop via csproj hygiene and protocol changes. The dominant root cause -- the Avalonia 11.0.11 XAML compiler defeating incremental builds -- is tracked separately as Phase 23.

### Phase 22A -- csproj hygiene [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 22A).

### Phase 22B -- Build protocol change [COMPLETED]

- □  
Update `docs/AGENT_RULES.md` build protocol. Current text prescribes `dotnet build --no-incremental` for every compile-time check, which forces a 15.56s full rebuild on every iteration.
- □  
Iteration: `dotnet build` (incremental) or `dotnet run`.
- □  
Pre-commit verification: `dotnet build --no-incremental -p:RunAnalyzersDuringBuild=true 2>&1 | Select-Object -Last 15`.
- □  
Same protocol update mirrored in `docs/RULES.md` build-vs-run section.

### Phase 22C -- VS Code workspace settings [COMPLETED]

- □  
`.vscode/settings.json` gains `"dotnet.backgroundAnalysis.analyzerDiagnosticsScope": "openFiles"` and `"dotnet.backgroundAnalysis.compilerDiagnosticsScope": "openFiles"`. Reduces editor-side memory and CPU on a solution this size.
- □  
Update VS Code C# Dev Kit extension to latest — 11.0+ claims up to 39x faster no-change incremental builds and 17x faster for a single changed C# file.
- □  
Note: this affects editor responsiveness only. `dotnet run` speed is unaffected by these keys.

### Phase 22D -- Measure and close [COMPLETED]

- □  
Re-baseline incremental and `--no-incremental` after 22A-B.
- □  
Acceptance: incremental loop < 4s, `--no-incremental` < 15s. If Phase 23 lands, target moves to < 2s incremental.

---

### Phase 22E -- Runtime diagnostic baseline [GUI THROTTLE SHIPPED 2026-10-25] -- MEASURED 2026-10-25 post-23A: incremental 3.62-4.47s (was 7.14s), CoreCompile skipped on no-op builds, remainder is MSBuild evaluation plus package-target overhead (181 imports, 1.72 MB evaluated XML, ONNX/Vortice/ComputeSharp/RapidOcr); bare evaluation alone 2.7-3.9s so two-second target is blocked by package surface, not compilation.

Captures CPU / GC / allocation numbers on the current build so Phase 23's impact on runtime is provable, not just its impact on build time. No code changes -- measurements only.

Tooling: `dotnet-counters` and `dotnet-trace` (free; no dotTrace / PerfView dependency).

- □  
Install: `dotnet tool install -g dotnet-counters` and `dotnet tool install -g dotnet-trace`.
- □  
Cold-start baseline: capture `[startup]` Stopwatch markers from the freshest `logs/runtime-*.log`.
- □  
Cache-load baseline: 600k-bar read time from `Cache: loaded ...` log line.
- □  
MC baseline: `dotnet-counters monitor -n backs2 --counters System.Runtime` during a single Optimize run at default settings.
- □  
Trace baseline: `dotnet-trace collect -n backs2 --duration 00:00:30` during the same run, then `dotnet-trace report <trace> topN -n 20`.
- □  
Record the outputs below. These are the numbers Phase 23 is compared against.

**Baseline (2026-10-09):**

Startup path (from `logs/runtime-*.log`, Phase 17 markers):

- DI container: 25 ms
- ConfigManager: 48 ms
- MT5 server: 6 ms
- MainViewModel: 22 ms
- MainWindow constructed: 230 ms
- **OnFrameworkInitializationCompleted total: 348 ms**

Runtime footprint (from `dotnet-counters collect --counters System.Runtime`, `logs/counters.csv`):

| Scenario ↕▾ | Working Set ↕▾ | CPU ↕▾ |
|---|---|---|
| −Idle, post-startup (all 12 tab views pre-warmed) | ~1500 MB | <1% |
| **During Optimize** | min 472 MB / max 2987 MB / avg 1338 MB | **max 76.5% / avg 22.4%** |
⚙

**Key finding — Optimize is not CPU-bound.** On a 20-core machine, a saturated `Parallel.ForEachAsync` would pin CPU near 100%. The capture shows 22% average and 76% peak. The optimizer is spending most of its wall time waiting — on disk I/O, on `StrategyManager`'s compile cache, on MC curve allocation, or on a serialized stage. Phase 23 is the wrong tool for this; it needs its own investigation.

**Follow-up:**

- □  
Locate the serialized stage in `OptimizationRunner.RunAsync` — the coarse MC loop and the refine loop are both sequential; only the per-combination backtest is parallel. That's likely where the wall time goes.
- □  
Capture during a 100+ version grid where the parallel phase dominates.
- □  
Compare CPU during the backtest phase vs. the MC phase, at least two separate captures with a marker in the log.
- □  
600k-bar cache load timing: still not captured — trigger explicitly via a Chart tab load during a counters session.
- □  
**GUI lag during optimization runs** (reported 2026-10-10). The main UI thread stutters while Optimize runs. Suspects, in order: (a) `Progress<OptimizationProgress>` fires 2000+ times per run and marshals each report through `SynchronizationContext` to the UI dispatcher, saturating it; (b) the status-text binding on every tick triggers a layout pass; (c) `IProgress` closures allocate per call. Fix direction: coalesce progress to ~10 Hz at the runner (not at the UI), and rate-limit the dispatcher post for status updates.

Captured via: `dotnet-counters collect -p <pid> --counters System.Runtime --output logs/counters.csv --format csv`. Raw CSV in `logs/` (git-ignored).

**Warning - Metreja profiler overhead is not neutral.** Running the optimizer under Metreja v1.0.23 slows each per-dataset Optimize run by roughly 24x (measured 2026-10-10: ~10 s baseline per dataset becomes ~4 min under profiler; a full multi-dataset batch that normally completes in ~1 min stretched to ~1.5 hours). The parallel MC topology is the cause: 20 concurrent worker threads each push enter/leave or method_stats events into the profiler's shared writer, which serializes on a buffer lock. Consequences: (a) wall-time and CPU% under the profiler are not comparable to unprofiled numbers; (b) Metreja is a hotspot *attribution* tool, not a perf-measurement tool. For perf measurement, use `dotnet-counters` or the in-app `[startup]` Stopwatch markers. Narrow the include to one namespace and use `method_stats` (not `enter leave`) to keep the trace under MB scale and the overhead under 2x.

## Phase 23: Avalonia 11.3 Upgrade [COMPLETED]

Root cause of the 7.14s incremental build. Avalonia PR #17539 (merged Nov 2024) reworked `AvaloniaBuildTasks.targets` so the XAML compiler runs as part of `CoreCompile` via `TargetsTriggeredByCompilation`, restoring incremental build correctness. The PR description states the previous behavior "made MSBuild think that the output was always changed between builds, effectively killing incremental builds." That fix shipped in the 11.3.x line; 11.0.11 predates it.

### Phase 23A -- Version bump [COMPLETED]

- □  
Bump in lockstep to 11.3.x (latest patch): `Avalonia`, `Avalonia.Desktop`, `Avalonia.Themes.Fluent`, `Avalonia.Fonts.Inter`, `Avalonia.Controls.DataGrid`, `Avalonia.Controls.ColorPicker`.
- □  
Verify `ScottPlot.Avalonia 5.0.55` resolves against 11.3.x; check the ScottPlot repo for the compatible Avalonia range before bumping.
- □  
No other package in `backs2.csproj` has an Avalonia coupling.

### Phase 23B -- Smoke test every touched surface

- □  
Shell chrome: `ExtendClientAreaToDecorationsHint` + `NoChrome`, fullscreen open, minimize, restore-from-taskbar.
- □  
DataGrid: Data tab grouping, group-header checkbox bulk select, column sort glyphs, filter row, row-level checkbox.
- □  
Color picker: `ColorPickerWindow` modal, `ColorSpectrum` / `ColorSlider` / `ColorPreviewer` rendering, hex input, brightness slider to zero, OK / Cancel.
- □  
ScottPlot: Chart tab candlestick + volume, Monte Carlo histogram / curves / cluster, keyboard pan/zoom handlers.
- □  
Popup-based controls: menu bar dropdowns, every ComboBox.
- □  
Theme: accent color change live, reset to defaults, restart persistence.
- □  
Menu bar: every navigation item routes to the correct tab.
- □  
Left nav rail: all 12 items select correctly and the accent stripe follows.
- □  
Cross-tab navigation: Monte Carlo cluster plot click jumps to Chart.

### Phase 23C -- Measure and close [COMPLETED]

- □  
Re-baseline incremental after bump. Target: < 2s.
- □  
If any smoke test fails, revert to 11.0.11 and close the phase as `[DEFERRED]` with the blocking reason recorded.

---

## Phase 24: Tab Responsiveness [IN PROGRESS] -- Phase 23 shipped 2026-10-25: core Avalonia packages 11.3.22, DataGrid 11.3.13 (versioning diverged), no API breaks, rebuild clean, incremental skips CoreCompile, 23B remains a manual smoke test.

Extends the Phase 21 pattern (off-thread, parallel metadata scan + fire-and-forget ctor refresh) to the remaining tabs whose ViewModels do disk I/O in their constructors. Diagnosis from 2026-10-08: only Data and Accounts are affected. Config, Metrics, Overview, Logs, Chart, Runner, and Monte Carlo do no disk I/O in ctor and are already responsive.

### Phase 24A -- Data tab [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 24A).

### Phase 24B -- Accounts tab [COMPLETED 2026-10-28]

- □  
`AccountService` ctor scans `data/accounts/` and parses each firm JSON. ~150 ms cold on first Accounts click.
- □  
Same pattern as 24A: async init + fire-and-forget from `AccountViewModel` ctor.
- □  
`MonteCarloViewModel` reads `AccountService.Active` at run start — needs a guard for the uninitialised state.
- □  
Optional: it is under the "noticeable freeze" bar; do only if the phase is otherwise cheap.

### Phase 24C -- Config tab [CLOSED - no change]

Provider discovery via `ActivatorUtilities.CreateInstance` completes in ~8 ms per the Phase 17 startup log. Nothing to fix. Closed for the record.

---

## Phase 12: Hardening & Housekeeping [PLANNED]

---

## Phase 28: Unified Computing Subsystem [PLANNED]

Introduces a central abstraction layer in `src/core/Computing/` for hybrid CPU/GPU workload dispatch. This sits behind the existing Compute Worker IPC boundary: the parent process dispatches domain-specific workloads to the worker, which schedules them across available hardware.

**Framework.** `ComputeSharp` (DirectX 12 backend). Chosen over ILGPU because it is .NET 8 native, uses source generators to compile kernels directly into the assembly (no runtime compilation step), and is available on public NuGet. The CPU path uses `System.Threading.Tasks.Parallel`.

**Preconditions.** Phase 26 must be fully closed. Compute Worker Phase 3 must be complete (the worker process is active and handles the 64 MB IPC pipe).

### Phase 28A -- Abstractions & Parent Dispatcher [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 28A).

- **Size:** S · **Depends on:** Phase 26 closed · **Succeeds when:** `IComputeDispatcher` exists in the parent process with domain-typed `DispatchMonteCarloAsync` / `DispatchBacktestBatchAsync` methods implemented against a DI-resolved `IComputeBackend`.
- ☑  
Define `IComputeDispatcher` with domain-specific methods: `DispatchMonteCarloAsync` and `DispatchBacktestBatchAsync`.
- Deferred: the parent-side `WorkerEnvelope` proxy. Compute Worker Phase 3 is not a scheduled phase, so there is no IPC path to proxy over — `CpuComputeDispatcher` resolved `IComputeBackend` from DI and delegated in-process. `MonteCarloDispatchRequest` stays a JSON-shaped record so the proxy is a wiring change, not a redesign.

### Phase 28B -- Parent-Process Hybrid Scheduler [COMPLETED]

- **Size:** M · **Depends on:** 28A (shipped) · **Succeeds when:** the parent-process dispatcher executes a Monte Carlo workload with chunked work-stealing, with per-iteration seed determinism preserved across chunk boundaries.
- ☑  
~~Create `src/core/Computing/Backends/` containing `IComputeBackend`, `CpuBackend`, and `GpuBackend` (probing for a DirectX 12 device, falling back seamlessly).~~ Shipped in 28A.
- ☑  
Implement a cooperative work-stealing scheduler in the parent process: `CpuComputeDispatcher` cuts an iteration count into 10k-iteration chunks, workers claim them off a `ConcurrentQueue<int>` until it is empty, and each chunk runs through `IComputeBackend.RunIterationsAsync` -- the new chunk-level backend seam -- so no caller-side branch decides CPU vs GPU. Non-vectorizable workloads bypass the queue and route strictly to `CpuBackend`.
- ☑  
Per-iteration RNGs are split on the absolute iteration index (`new Random(seed + i)`), so the chunked path is bit-identical to `CpuBackend.RunSimulationsAsync` and survives chunk-boundary changes.
- Moved to `docs/ROADMAP_ARCHIVE.md` (Phase 28B). Compute Worker Phase 3 is not a scheduled phase, so the worker migration is deferred to a later phase; the dispatcher surface is deliberately IPC-shaped so that move is wiring, not a redesign.

**Rejection list** (explicitly out of scope for the computing subsystem):

- **`IMetric.Calculate`** -- sequential time-series dependency chains (EMA, etc.); GPU cores would starve.
- **`KMeansClusterer.Cluster`** -- N = 20-500 trades. Dispatch overhead heavily exceeds compute time. Constrained to fixed seed 42, making GPU wavefront determinism an unnecessary risk.
- **`MonteCarloEngine.BuildResult`** -- CPU radix sort handles 100k arrays in milliseconds; GPU memory round-trip is pure overhead.

---

## Phase 29: Compute Retrofit [PLANNED]

Retrofits existing CPU-bound and parallel workloads onto the Phase 28 subsystem.

### Phase 29A -- Instrumentation & Bottleneck Verification [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 29A).

- **Size:** S · **Depends on:** 28A (shipped) · **Succeeds when:** telemetry conclusively identifies the serialized stage causing the 22% CPU average during Optimize runs (Phase 22E finding).
- ☑  
Add granular `Stopwatch` metrics around the disk I/O, `StrategyManager` compile cache, and time-slice allocation phases of the Optimizer loop.
- ☑  
Log these phases to `logs/runtime-*.log` and do not proceed to 29C until the I/O-bound vs CPU-bound split is mathematically proven.
- Result: the split is proven **CPU-bound** (95.3% of a 20-core ceiling in thread-summed work, 97.3% cold), and Phase 22E's four suspects are all wrong. The backtest phase owns 66-81% of wall time; the disk read plus compile-cache lookup is under 1%; the coarse MC loop is not the serialized stage. 29B/29C proceed, but 29C is a placement change rather than a throughput one. Full numbers in the archive entry.

### Phase 29B -- Monte Carlo GPU Retrofit [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 29B).

- **Size:** L · **Depends on:** 29A, 28B · **Succeeds when:** `MonteCarloEngine.SimulateOnce` executes via `IComputeDispatcher`, utilizing `ComputeSharp` `[Shader]` kernels when the worker holds the GPU, with results statistically identical to the pure CPU path.
- ☑  
Port the exact MTM-low vs max-DD floor state machine into `SimulationKernelGpu` — one ComputeSharp `[Shader]` dispatch per chunk, one GPU thread per simulation, with Philox-4x32 seeded per thread from `(seed, absolute iteration)`. `MonteCarloEngine.SimulateOnce` moved verbatim into `SimulationKernelCpu`; the engine now keeps only parameter derivation, the `MaxIterations` clamp, the `MaxCurveCells` ceiling, aggregation and `BuildResult`.
- ☑  
Enforce the Phase 27 determinism constraints: the RNG is split on the absolute iteration index so chunk boundaries never move a result, iteration counts are chunked at the scheduler's 10,000 (`GpuIterationBatch`) under the Windows TDR limit, and `MaxDegreeOfParallelism` survives end to end.
- ☑  
Dispatch in process: `GpuBackend` probes DirectX 12 in its constructor, never throws, reports `IsAccelerated = true` only when a hardware device *and* the kernel were created, and re-runs a failing chunk on `CpuBackend` for the same absolute iteration range. No IPC, no worker round-trip.
- ☑  
Widen the backend contract to the shipped result shape: `IComputeBackend` / `IComputeDispatcher` carry `MonteCarloDispatchResult` — nine parallel per-iteration arrays plus optional equity curves — instead of a `double[]` of terminal balances, and `backs2.csproj` gains `AllowUnsafeBlocks` for ComputeSharp's shader source generator (CMPS0052).
- 29B.1 (batch the Monte Carlo dispatches) is **pending, not landed**: checked head 0bcc0e4 and every ref on 2026-10-10 -- no commit implements it and no numbered block exists for it, so no `[COMPLETED]` block is created here. Until it lands, the CPU backend remains the correct resolved backend for the coarse pass.
- Result: **the GPU kernel is correct but loses the current optimizer workload as dispatched.** Per iteration the kernel is **10-16x faster** than the CPU path (100,000 iterations: 480-519 ms GPU vs 6,392-6,714 ms CPU); on the Phase 29A coarse pass (240 versions x 100 iterations) the GPU loses **~5x** (2,307-2,361 ms vs 261-487 ms warm), because the coarse pass is 240 separate tiny dispatches and only **4.4%** of the phase is kernel work — the other **95.6%** is slice mirroring, upload, readback and nine output-buffer allocations per call. GPU-vs-CPU is statistically equivalent within 3 standard errors on 8/8 seeds (10,000 iterations x 1,796 slices), so the loss is workload shape, not kernel quality. **Batching the coarse MC dispatches is the follow-up (Phase 29B.1, not yet scheduled).** Full numbers in the archive entry.

### Phase 29B.1 -- Batched Monte Carlo Dispatch [COMPLETED, THROUGHPUT UNMEASURED]

Batch the per-version Monte Carlo dispatches so the ~10 ms fixed cost per dispatch (slice mirroring, upload, readback, nine output-buffer allocations) is amortized across all versions in a single kernel launch. This is the fix that makes the GPU path beat the CPU on the current optimizer workload; without it the GPU is correct but loses on the coarse pass by ~5x. **Not yet scheduled** — noted here so the follow-up from Phase 29B's result line has a home. The hypothesis was that batching would make the GPU beat the CPU on the optimizer coarse pass; measurement falsified it at that shape (CPU-batch wins, GPU-batch needs far more iterations per version to break even). It requires restructuring the optimizer loop to collect a batch of version requests before dispatching, which is out of scope for any shipped phase. Shipped 2026-10-25 in two stages. Stage 1 (c2dd416) added IComputeDispatcher.DispatchMonteCarloBatchAsync - one flat worker pool over all versions, longest-first, each item running the unchanged single-request path at MaxDegreeOfParallelism=1 - plus MonteCarloEngine.CreateRequest/RunBatch and the optimizer coarse loop collecting requests before dispatching. Stage 2 (f96616a) added the GPU side: the kernel gains a batch mode with one flat GpuTimeSlice buffer plus per-thread slice-base/count/seed buffers, dispatching all versions in a single launch. The dispatcher tries the GPU batch first when versions share one MonteCarloSimulationRequest and curves are not retained, and falls back to the flat CPU pool otherwise. Determinism is proven by the --batch-verify harness runs 24 heterogeneous versions x 100 iterations and compares all nine output arrays between batched and per-version dispatch: 0 mismatches, on both backends. Because CPU and GPU RNGs cannot produce identical doubles, a zero-mismatch GPU result proves both paths ran the same kernel. The batch RNG is keyed on (version seed, iteration within version) - a first cut using the flat batch index would have silently diverged every version after the first. Measured 2026-10-25 with the --mc-bench hook (synthetic slices, 1500-2400 per version, same seeds, three dispatch shapes): 240x100 = 472/48/70 ms; 500x100 = 809/80/124 ms; 500x1000 = 5295/649/620 ms; 500x5000 = 25433/3103/2761 ms (cpu-per-version / cpu-batch / gpu-batch). The CPU batch is the real win at every scale: roughly 10x faster than serial per-version dispatch, from the flat pool and longest-first ordering in Stage 1. The GPU batch does NOT win at the optimizer's actual coarse-pass shape (100 iterations per version) - it is 1.5x slower at 240x100 and 500x100. It reaches parity at 500x1000 (620 vs 649 ms) and wins modestly at 500x5000 (2761 vs 3103 ms, 1.12x). The crossover is kernel-work-per-upload, exactly as the dispatch-shape analysis predicted; it just sits far above the coarse pass. Conclusion: CPU-batch is the correct backend for the optimizer coarse pass, and the GPU path should not be claimed as a coarse-pass speedup. The GPU keeps its win on large single dispatches (100k iterations, unchanged) and on any future workload with high iterations per version.

### Phase 29B.2 -- GPU Chunk Aggregation Fix, Backend Toggle, Log Hygiene [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 29B.2).

- **Size:** S · **Depends on:** 29B · **Succeeds when:** the DirectX 12 chunk dispatch no longer throws the ComputeSharp pipeline-cache duplicate-key exception under the parallel optimizer workload, the Monte Carlo backend can be forced to CPU at runtime, and repetitive compute-path log lines are deduplicated per run.
- ☑  
Fix the `SimulationKernelGpu` first-dispatch race: a process-wide `ConcurrentDictionary<GraphicsDevice, Lazy<FirstDispatchGate>>` gate, with `LazyThreadSafetyMode.ExecutionAndPublication` so the gate is singular per device even when `GetOrAdd` invokes its factory twice under contention. The root cause is ComputeSharp 3.2.0's `PipelineDataLoader<T>.CreatePipelineData` using `CachedPipelines.Add(...)` where it should use `GetValue`/`AddOrUpdate`; the gate serializes only the first dispatch per device and short-circuits on every subsequent one.
- ☑  
Add `AppSettings.MonteCarloBackend` (`Auto`/`Cpu`/`Gpu`, default `Auto`) plus `AppSettings.MonteCarloBackendValues` and `ConfigManager.NormalizeBackend(string?)`. `CpuComputeDispatcher.ResolveBackend` reads it per dispatch; `Cpu` short-circuits device probing entirely, `Gpu` logs one Warn and falls back to CPU if the probe failed, `Auto` uses the accelerated backend when available. `GpuBackend`'s probe moved to a `Lazy<GraphicsDevice?>` so the `Cpu` path never touches the device.
- ☑  
Add the Runner UI: a labeled `ComboBox` in the MC settings panel, bound to `RunnerViewModel.McBackend` / `McBackendOptions`, normalizing and persisting through `ConfigManager.SaveConfig()` on change.
- ☑  
Add `LogManager.LogOnce` — a per-run `ConcurrentDictionary<string, byte>` seen-set with `Warn`/`Error`/`DebugMsg` entry points, plus `RecordFallback`/`RecordDispatch`/`FlushFallbackSummary` for the run-level fallback count. Route the GPU chunk-failure path, the cancellation line, and the "Gpu requested but no device" path through it. Remove the per-chunk success Debug line that fired 1,841 times per run in the 926 KB Phase 29B log.
- ☑  
Verification: `--mc-verify --backend=Auto --dispatches=8 --iterations=100` (no warmup, cold gate) reports `duplicateKeyFirstChanceExceptions=0` and exit code 0; the `Auto` path resolves to `backend=GPU (NVIDIA GeForce RTX 4060 Laptop GPU)` on the dev machine; the `Cpu` path never touches the device.
- The throughput delta on the full 500-combination workload is **not** measured in this phase — correctness and control are the deliverables; Phase 29B.1 remains the throughput lever.
- Note: `--mc-verify` stays in tree as a diagnostics hook (mirrors `--verify-strategy-parallelism` and `--compute-selftest`). Full numbers in the archive entry.

### Phase 29C -- Optimizer Backtest Retrofit [COMPLETED] -- shipped 2026-10-25 (709fe31): new DispatchBacktestBatchAsync overload takes the per-job body as a Func so the dispatcher owns scheduling and the caller owns the work; the original skeleton took only a count and could not express what a job is. One flat Parallel.ForEachAsync pool, CPU path only, since Roslyn backtests are neither chunkable nor vectorizable. OptimizationRunner injects IComputeDispatcher and its backtest phase dispatches through it. Worker-process migration not done -- that is wiring behind the interface.

- **Size:** M · **Depends on:** 29A, 28B · **Succeeds when:** the per-combination backtest loop executes via `IComputeDispatcher`, routing exclusively to the worker's CPU backend.
- ☑  
Wire `OptimizationRunner` to dispatch Roslyn script backtests in batches to the worker.
- ☑  
The worker routes these batches strictly to `CpuBackend` (Parallel.For), as strategy scripts are inherently non-vectorizable.

### Phase 30 -- Roslyn Backtest Cost Investigation [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 30).

- **Size:** S · **Depends on:** 29A · **Succeeds when:** the compile-vs-execute split inside `ScriptRunner` is a measured number rather than a derived residual.
- ☑  
Add accumulated stage timers to `StrategyManager` (`CompileTimeMs`, `CacheLookupTimeMs`, `InvokeTimeMs`, `GlobalsSetupTimeMs`, plus hit/miss counters), resettable per run and read once at summary time.
- ☑  
Emit one `[30]` summary block from `OptimizationRunner` alongside the existing `[29A]` block.
- ☑  
Re-run the Phase 29A workload unchanged: 500 combinations, `EtWindow` on `GOLD.pro@30m`, 20 cores, cold pass then warm pass.
- Result: **the warm path is invoke-bound and the invoke cost is mostly Roslyn scripting-API overhead, not strategy-internal work.** Warm backtest phase 1,081 ms of a 3,255 ms wall (33.2%); inside it, invoke is 19,796 of 21,209 ms thread time (93.3%), compile is 0 (500/500 cache hits), and stat + source read + cache lookup are 1,195 ms (5.6%). Probes put the strategy's own body work at 11.03 ms/version against a 39.59 ms/version invoke — **72% of every invoke is Roslyn plumbing**. The cold pass inverts it: compile 126,284 of 151,663 ms thread time (83.3%), because 20 of 20 first-wave workers miss the cache and compile the same script concurrently. Phase 29A's "38 ms derived residual" was correct but hid all of this — it could not see inside `ScriptRunner`.

### Phase 31 -- Thread-Safe Single-Flight Compile [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 31).

- **Size:** S · **Depends on:** 30 · **Succeeds when:** concurrent workers that miss the same compile-cache key produce one compile rather than one each, with distinct keys still compiling in parallel and the warm path untouched.
- ☑  
Single-flight the miss path in `StrategyManager`: a per-key `SemaphoreSlim` gate (mirroring the file's existing `_throttle` pattern) plus a double-checked cache read, awaited, released and dropped in `finally`. No global lock, no `lock (dict)`, no new cache.
- ☑  
Log one Debug line when a compile actually starts, so a cold log greps to one line per distinct key instead of one line per miss. No Info-level logging, no new `LogTag`.
- ☑  
Re-run the Phase 30 workload unchanged: 500 combinations, `EtWindow` on `GOLD.pro@30m`, 20 cores, cold pass then warm pass, each pass compared element-by-element against the archived `batch_20261010-161522.json`.
- Result: **the cold compile stampede is closed — 20 compiles became 1, and 126,017 ms of compile thread time became 5,377 ms (−95.7%)**, with the cold lookup classification unchanged at 480 hits / 20 misses, all 500 parameter sets, trade counts and net PnL matching the archived 29A batch exactly (0 mismatches on both passes), and the warm pass untouched (500/500 hits, 0 compiles, 45.97 ms/version invoke against 48.88 ms/version before). Cold wall 9,500 → 8,526 ms (−10.3%): 20 compiles of one script overlap across 20 cores, so the stampede cost thread time and machine load far more than wall — the wall saving is one uncontended compile instead of one compile under 19-way contention. Full numbers in the archive entry.

### Phase 32 -- EasternTime Conversion Fast Path [COMPLETED]

- **Size:** M · **Depends on:** 30, 31, 33 · **Succeeds when:** the per-version warm invoke drops materially from the measured 16.65-20.63 ms/version on the Phase 30 workload, with the ET conversion of all 60,540 bars bit-identical to `TimeZoneInfo.ConvertTimeFromUtc` and byte-identical `Trade` sequences.
- Shipped 2026-10-25 (9e032c2). EasternTime.ToEastern now resolves the America/New_York offset from a per-year transition table built lazily on first access and cached per year, instead of calling TimeZoneInfo.ConvertTimeFromUtc per bar. The hot path is a loop over one or two transitions plus a DateTime offset add. Boundaries are located by hour-stepping the year and bisecting to the minute. Verified bit-identical, not sampled: --et-verify probes 500,000 random instants in [1990, now) against TimeZoneInfo.ConvertTimeFromUtc and reports 0 mismatches, both DST boundaries included; the hook stays in tree as the regression check. Not measured: the end-to-end per-version invoke win on the Phase 30 workload - correctness is proven, the throughput claim is untested. Phase 33's decomposition retargets this phase. It was specced as a Roslyn bypass -- replace `ScriptRunner<T>` with a pre-bound delegate over a fixed globals shape -- on the strength of Phase 30's "72% of every invoke is Roslyn scripting-API plumbing". Phase 33 falsified that premise: the delegate call is 100.0% of invoke, the pre-invoke plumbing is 1 ms over 500 versions, and an empty script pushed through the identical `CSharpScript.Create` -> `CreateDelegate` -> `runner(globals)` path costs below the measurement floor. The invoke *is* the script body, and the body's cost is `EasternTime.ToEastern`'s per-bar `TimeZoneInfo.ConvertTimeFromUtc`, **60,540 calls/version**. A Roslyn bypass therefore removes nothing. The scripting-API half of this phase is dropped, not deferred.
- ☑  
**Approach (stub; the full spec follows after `src/core/Utils/EasternTime.cs` is read).** Resolve the `America/New_York` offset from a pre-built DST-transition table instead of a per-bar `TimeZoneInfo.ConvertTimeFromUtc`: enumerate `TimeZoneInfo.FindSystemTimeZoneById("America/New_York").GetAdjustmentRules()` once per process, per UTC year, and select the applicable offset by comparing the bar's UTC instant against the transition boundaries rather than performing a time-zone lookup. The lookup must stay read-only and thread-safe for the 20 concurrent backtest workers, and every bar's ET result must be checked against the current implementation, not sampled.
- Note: this phase's own success criterion cites the warm invoke as a range, not the single 39.59 ms/version figure the Roslyn-bypass spec was written against -- Phase 33 measured the same workload 2x faster because the host was less loaded. The share (100% of invoke is body execution) is the finding; the absolute warm figure needs a quieter machine to be a target. See the Phase 33 archive entry.

### Phase 33 -- Roslyn Invoke Cost Decomposition [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` (Phase 33).

- **Size:** S · **Depends on:** 30 · **Succeeds when:** the Phase 30 invoke number is split into "the arguments handed to the delegate" and "the delegate call itself", measured rather than derived.
- ☑  
Subdivide the `[30]` invoke stage in `StrategyManager` at the only seam the scripting API exposes from outside: `InvokePlumbingTicks` (TradeRecorder + RunnerScriptGlobals construction and the argument handoff) and `InvokeCallTicks` (the delegate call), both additive with the unchanged `InvokeTicks` so the `[30]` numbers stay comparable.
- ☑  
Emit one `[33]` block from `OptimizationRunner` alongside `[29A]`/`[30]`: total invoke ms, pre-invoke plumbing ms, delegate call ms, each as a share of the invoke span, plus a per-version line and an additivity line.
- ☑  
Re-run the Phase 30 workload unchanged: 500 combinations, `EtWindow` on `GOLD.pro@30m`, 20 cores, warm pass, all 500 versions compared element-by-element against the archived 29A batch.
- Result: **the delegate call is 100.0% of invoke, and the delegate call is the script body -- pre-invoke plumbing is ~0 ms.** Plumbing measured 1 ms total over 500 versions (0.00 ms/version); the delegate call measured 8,322-10,316 ms of the 8,323-10,317 ms invoke across the verification runs (16.64-20.63 ms/version). An out-of-process probe through the identical `CSharpScript.Create` -> `CreateDelegate` -> `runner(globals)` path priced an empty script at 0.000 ms median per call and the real `EtWindow` source at ~11 ms on an idle machine -- below the scripting-API measurement floor. **Phase 30's 35.76 ms/version "Roslyn plumbing" attribution was a measurement artefact**, not a cost: that probe ran on an idle machine while the production run was contended. The hot path is `EasternTime.ToEastern`'s per-bar `TimeZoneInfo.ConvertTimeFromUtc` (60,540 calls/version). This kills the Roslyn-bypass design and redirects Phase 32 to an `EasternTime` fast path. Full numbers in the archive entry.

---

## Phase 37: Monitor Switching [COMPLETED - candidate 2, 2026-10-28]

Moving the backs2 window between monitors (drag, or Win+Shift+Left/Right) needs verification, and probably reactive handling on DPI change.

Open question for the user - which scope is intended?

1. Verify only. Avalonia should already handle DPI changes. Test on mixed-DPI pair; ship if green.
2. React on ScalingChanged. Subscribe on MainWindow, force repaint of shell chrome, ScottPlot controls, and DataGrid when the window crosses a DPI boundary.
3. Recreate window. Heavy. Not recommended.
4. Fullscreen round-trip. Highest risk. Verify FullScreen -> Normal -> move -> FullScreen.

Test plan (verify candidate 1 first):

- Shell chrome renders at correct scale after move.
- ScottPlot (Chart, Monte Carlo histogram / curves / cluster) - no blur, clipped axes, or frozen canvas.
- DataGrid (Data tab) - column widths, row heights re-scale.
- ColorPicker modal (Config) repositions to parent monitor.
- Pan/zoom handlers still respond.
- Fullscreen -> restore -> move -> fullscreen round-trip.
- Hostile pair: 100% primary + 150% secondary, both directions.

Likely files if candidate 2 lands: MainWindow.axaml.cs (ScalingChanged), MonteCarloView.axaml.cs / ChartView.axaml.cs (re-render).

---

## Phase 38: Streamed GUI Loading [PLANNED]

Pattern: do not freeze-load-unfreeze. Stream content in batches and yield to the dispatcher between them, exactly as LogsViewModel does after Phase 22E (bounded Queue of LogEntry + coalesced flush on a DispatcherTimer + Interlocked.Increment frame hook).

Architectural shape: create a reusable core module before touching any tab. Name and location follow the precedent set by src/core/Computing/ - a self-contained core subsystem the rest of the app consumes as a service. Working name: src/core/GuiLoading/ (final name up to the implementer). This is a REFACTORING phase, not a patch. Do not scatter a helper across each ViewModel. Build the loader once as a core subsystem, register in DI, route every tab through it.

Sub-phases:

- 38A - Core module. SHIPPED 2026-10-28. src/core/GuiLoading/StreamingLoader<T> + IStreamingLoaderFactory + README. Registered as singleton in App.axaml.cs. Bounded batch size (default 100), latest-wins cancellation, sink on UI thread, producer on worker. Unit tests deferred to 38B when a real consumer exercises it.
- 38B - Refactor Data tab. DataViewModel.RefreshData / RefreshDataAsync routes through the loader. Group-header rows and member rows stream in together (header first, members after). Accept: grid interactive within 100 ms, fully populated within 500 ms at 10x current cache count. Fix the known visual bug (see below) while touching this file.
- 38C - Refactor Accounts tab. 54 descriptors currently materialised on selection change. Stream them in. Same acceptance.
- 38D - Refactor Providers tab. Parallel metadata scan is already off-thread in RefreshAsync; route the aggregation + Groups.Add loop through the loader. Same acceptance.
- 38E - Audit remaining tabs. Mt5 PopulateGrid (already batched via RangeObservableCollection.AddRange - verify), ChartViewModel dataset swap, any other ObservableCollection fill path. Convert any remaining freeze-then-unfreeze shape.
- 38F - Acceptance. Every tab interactive within 100 ms of click on warm cache; fully populated within 500 ms at 10x current dataset count. Instrument via existing LogTag.Data / LogTag.Cache / LogTag.Account Stopwatch markers.

Design constraints:

- No new framework. The loader is a thin wrapper over ObservableCollection + DispatcherTimer + a bounded Queue - the same primitives LogsViewModel already uses.
- Cancellable. Tab switch or refresh click during a streamed load cancels the in-flight batch. Latest-wins semantics, matching the existing CancellationTokenSource pattern in DataViewModel.
- ObservableCollection stays as the binding contract. No XAML change required; only the fill pattern changes.
- Complements Phase 24 (off-thread disk I/O). 24 unblocks the disk; 38 unblocks the dispatcher.

Known issue carried from Pending Testing #8 (2026-10-28): the Timeframe column header label and its sort glyph are clipped ("Timefram" + truncated triangle). Likely a fixed-width ColumnDefinition in DataView.axaml. Fix in 38B while the Data tab is being touched.

---

## Phase 39: Startup Latency [COMPLETED 2026-10-28 - workflow answer]

Reduce the wall time between `dotnet run` returning control and the app window first rendering.

Current state (measured 2026-10-28): Program.Main -> LogManager.Initialize 41ms, DI 22ms, ConfigManager 38ms, MT5 server 6ms, MainViewModel 22ms, MainWindow construct 232ms, OnFrameworkInitializationCompleted total 333ms. In-process total ~591ms (Program.Main entry to end of OnFrameworkInitializationCompleted). Gap of ~258ms between LogManager.Initialize and OnFrameworkInitializationCompleted is Avalonia framework init plus App.axaml XAML load. Two dominant costs: XAML load ~258ms (44 percent), MainWindow construct 232ms (39 percent).

Warm `dotnet run` adds ~3.6s of MSBuild + Roslyn ahead of that ~591ms (Phase 22D). The `dotnet run` build wall is the dominant cost of the loop, not the in-process startup.

Answers (2026-10-28):
1. Warm exe launch -> first frame: ~591ms in-process. Warm `dotnet run` -> first frame: ~4.2s (build dominated).
2. Bottleneck is (a) `dotnet run` compile phase for the dev loop (86 percent of wall), then the in-process Avalonia XAML load (~258ms) and MainWindow construct (~232ms).
3. Both. Use the exe directly for the fastest loop.

Possible directions, ranked by expected impact per effort:
- Use `dotnet build` once, then `.\bin\Debug\net8.0\backs2.exe` for iteration instead of `dotnet run` — skips the compile step entirely. Zero code change.
- Defer Avalonia XAML load until after DI resolves (currently `App.Initialize` loads XAML before `OnFrameworkInitializationCompleted` builds DI).
- Split MainWindow construct (currently ~230ms) into chrome-first + content-lazy; only Overview tab's VM materialises at startup today (Phase 17 already did this).
- Pre-warm StrategyCatalogService's `FileSystemWatcher` asynchronously instead of synchronously.

Deferred: cold-start (first run after clean) is a different problem — MSBuild + NuGet restore dominate. This phase targets warm iteration only.

Known dependencies: needs the Phase 17 `[startup]` instrumentation to add one more marker — `Program.Main` entry to `LogManager.Initialize` — so the true in-process startup is bracketed. MARKER SHIPPED 2026-10-28: Program.Main now brackets LogManager.Initialize and the Avalonia lifetime wall with [startup] Stopwatch markers. Runtime capture: ~591ms in-process total, measured 2026-10-28 (see Current state above). Dominant costs are Avalonia XAML load and MainWindow construct; both resist easy optimization.

## Phase 40: Walk-Forward UI Wiring [COMPLETED 2026-11-11]

Adds walk-forward optimization to the Runner tab: a "Walk-Forward Settings" expander (IS/OOS lengths, step, mode, objective, carry top N), a "Walk-Forward" toolbar command, and a "Walk-Forward Results" subtab (index 2) with batch picker, sort, cap, refresh, open folder, clear, and a window DataGrid.

New code: `src/core/WalkForward/` (models, planner, objective, aggregator, JSON context, store, runner), `src/gui/Runner/RunnerViewModel.WalkForward.cs`, `RunnerViewModel.WalkForwardResults.cs`, `WalkForwardWindowRow.cs`, `BoolToForegroundConverter.cs`.

Modified: `RunnerView.axaml` (expander + results grid + toolbar button), `RunnerViewModel.cs` (ctor deps, subtab index 2 flag), `App.axaml.cs` (WalkForwardStore + WalkForwardRunner singletons; IS/OOS delegates wired to `OptimizationRunner.RunAsync(persist: false)`), `MainViewModel.cs` (Walk-Forward Results navigation node), `ConfigManager.cs` (AppSettings fields + normalizers), `OptimizationRunner.cs` (trailing `persist` flag on `RunAsync`).

Persistence: batches written to `data/walkforward/` via `WalkForwardStore`; schema v1.

Verified: `dotnet build -c Release` introduces no new errors (the 44 DataGrid/style AVLN2000 errors are pre-existing and unrelated).
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