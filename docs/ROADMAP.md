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

## Phase 26: Parameter Optimizer & Version Store [PLANNED]

Extends the Runner into a parameter-search tool. The user declares min / max / increment per numeric `@param`; the optimizer generates a bounded Cartesian grid, backtests each version, runs Monte Carlo per version, and surfaces the results in a repurposed Chart tab. Removes the source-preview pane, folds MC settings into the Runner, deletes the Monte Carlo tab, and deletes `NavigateToTradeMessage` (its cluster-plot publisher goes with the MC tab).

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
- [x] **26B.1 Verification [PASSED]:** ScriptRunner is thread-safe; the StrategyManager throttle can be bypassed by the optimizer. Version-level parallelism is viable. See `docs/ROADMAP_ARCHIVE.md` (Phase 26B.1).
- [ ] `src/core/Optimizer/ParameterGridBuilder.cs` — Cartesian product from min / max / increment per parameter. Hard cap enforced here; random-sample fallback when over cap.
- [ ] `src/core/Optimizer/OptimizationRunner.cs` — the per-version loop: backtest → build `TimeSlice[]` → MC at coarse iterations → emit partial result → next version. After the loop completes, refine top-N survivors at full iterations.
- [ ] Progress reporting: fraction of total work per stage (grid → backtest per version → MC per version → refine pass). Reported via `IProgress<OptimizationProgress>`.
- [ ] Cancellation: single global `CancellationTokenSource` for the whole run, flowing into every backtest and MC call.

### Phase 26C -- ETA estimator

**Parallel-topology fix (2026-10-10).** The optimizer now runs coarse and refine MC under an outer-parallel loop with `outer = min(combos, cores)` and `inner = cores / outer`. The current `EtaEstimator.Estimate` multiplies `remainingCombos` by an average per-version time as if the loop were sequential - every estimate is over-reported by the outer degree (~20x on a 20-core box).

- [ ] Rewrite `EtaEstimator.Estimate` so each phase's contribution is divided by its outer degree: `(remainingBacktests / W_backtest) * avgBacktest + (remainingCoarse / W_coarse) * avgCoarse + (remainingRefine / W_refine) * avgRefine / gpuFactor`.
- [ ] Thread phase-specific outer degrees through from `OptimizationRunner` to `EtaEstimator`. Each phase currently computes its own `outer` locally - pass it in.
- [ ] Expose a single `EstimateTotal()` that returns one wall-time number covering the **whole** run (backtest + coarse MC + refine MC + save) from the moment the grid starts building. The Runner's ETA display should read one number, not stitch per-phase pieces together at call sites.
- [ ] Normalize the moving-average sample per unit of work (ms per version, ms per simulation) rather than ms per wall-clock, so the average stays valid across topology changes.
- [ ] `src/core/Optimizer/EtaEstimator.cs` — moving average of backtest and MC times, updated after each version. Blind for the first 5 versions, then extrapolates.
- [ ] Formula: `ETA = remainingCombos * (AvgBacktestTime + AvgMCTime / gpuAccelerationFactor)`.
- [ ] UI: `TextBlock` in the Runner, updated every ~5 seconds via a throttled timer to avoid UI thread jitter.
- [ ] Per-parameter table extension: `RunnerParameterViewModel` gains `Min`, `Max`, `Increment` observable properties, a derived `ValueCount`, and a per-row preview.
- [ ] `gpuAccelerationFactor` reads from a new `AppSettings.OptimizerGpuFactor` setting (default 1.0). Not exposed in the UI yet; the setting exists so the future GPU path is a value change, not a schema change.

### Phase 26D -- Runner UI rework
- [x] **26D.1 -- Ship MC settings first [COMPLETED]:** MC settings panel added to Runner, backed by `RunnerViewModel` fields; active account banner and ruin threshold auto-update on `AccountService.ActiveChanged`. MC tab untouched.
- [x] **26D.2a -- Chooser + source viewer [COMPLETED]:** Source-preview column removed. Two-column layout: filter box + `ListBox` chooser + `View Source...` on the left, parameter panel on the right. `StrategySourceWindow` (`src/gui/Strategy/`) opens read-only source on demand. Diff-based `FilteredStrategies` update so filtering does not null the selection.
- [ ] **26D.2b:** Add min/max/increment fields per numeric `RunnerParameterViewModel`; live combination-count per parameter and per grid; cap warning when total exceeds `OptimizerMaxCombinations`.
- [ ] **26D.2c:** Add `Optimize` button next to `Run`; wire to `OptimizationRunner.RunAsync`; progress bar + ETA display driven by `OptimizationProgress`.
- [ ] `RunnerParameterViewModel` gains min / max / increment fields per numeric parameter. Derived `ValueCount` per parameter, `TotalCombinations` across all parameters. UI blocks `Optimize` when `TotalCombinations` exceeds the cap and offers a random-sample mode.
- [ ] Add an `Optimize` button next to `Run`. `Run` keeps single-configuration behavior. `Optimize` runs the grid.
- [ ] ETA display and progress bar wired to the optimizer.
- [ ] Optimizer settings block (cap, coarse iters, refine iters, refine top N) added to the Runner's MC settings panel.

### Phase 26E -- Monte Carlo tab removal
- [ ] Delete `src/gui/MonteCarlo/MonteCarloViewModel.cs` and partials `.Plots.cs`, `.Stats.cs`, `.Cluster.cs`.
- [ ] Delete `src/gui/MonteCarlo/MonteCarloView.axaml` + `.axaml.cs`.
- [ ] Evaluate `src/core/Analysis/KMeansClusterer.cs` for orphan status; delete if the cluster plot was its only consumer.
- [ ] Delete `src/gui/Messages/NavigateToTradeMessage.cs`.
- [ ] Remove `IRecipient<NavigateToTradeMessage>` from `MainViewModel` and `ChartViewModel`; delete both `Receive` implementations.
- [ ] Remove the Monte Carlo nav-rail item from `MainWindow.axaml`. **`ActiveTabIndex` shifts: Metrics 3→2, Accounts 4→3, Data 5→4, Data Automation 6→5, MT5 Ticks 7→6, Providers 8→7, Chart 9→8, Logs 10→9, Config 11→10.** Update every hardcoded index in `MainViewModel.Shell.cs` navigation commands. Audit for any other reference to index 2, 9, 10, or 11.
- [ ] Unregister `MonteCarloViewModel` from `App.axaml.cs` (`ConfigureServices` and `Application.DataTemplates`).

### Phase 26F -- Chart tab repurpose
- [ ] `ChartViewModel` drops `Series`, `SelectedDataset`, `SelectedTimeframe`, all crosshair fields, `SetPlotControl`, and the `IRecipient` implementation. No longer consumes `SelectionQueue`.
- [ ] New shape: `Batches` (list of optimization batch files on disk), `SelectedBatch`, `Versions` (per-version rows), `SelectedVersion`, `VersionDetail` (stat block + terminal-balance histogram + representative curve).
- [ ] Left column: version `DataGrid` — combination index, one column per parameter, PRuin, PFunded, EdgePercent, median terminal balance. Sortable on every column.
- [ ] Right pane: selected-version stats (the full 30-field block), static histogram image, static curve image. Renders generated off the UI thread and cached per version.
- [ ] Bottom strip: sparkline images, one per version, up to a few hundred. Static; generated lazily.
- [ ] Delete `ChartPlotBuilder.cs`, `ChartSeriesFactory.cs`, `ChartAxisFactory.cs` if unused after the rework.
- [ ] Remove the keyboard pan/zoom tunnel handler from `ChartView.axaml.cs` (no interactive plots to navigate).

### Phase 26G -- Compute worker migration
- [ ] Move `OptimizationRunner` execution into the Compute Worker (`backs2.exe --worker`). Belongs with the eventual Compute Worker Phase 3 migration. Out of scope for 26A-F.

### Deferred -- GPU acceleration
See `docs/ARCHITECTURE.md` "Future: GPU acceleration" note (added with Phase 26's first commit). Summary: CPU-only today via `Parallel.For` inside `MonteCarloEngine.Run`; future path via a compute framework such as `ILGPU` or `ComputeSharp` for the hot `SimulateOnce` loop; keep `MonteCarloEngine.Run`'s public surface stable via an `IComputeBackend` parameter or a CPU/GPU split behind a common interface. `AppSettings.OptimizerGpuFactor` (26C) is the placeholder hook that lets the ETA model project a GPU-enabled run without a schema change.

---

## Phase 27: GPU-Accelerated Monte Carlo [PLANNED]

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

### Phase 27A -- Backend abstraction & CPU parity
- **Size:** S · **Depends on:** Phase 26 closed; Compute Worker Phase 3 complete · **Succeeds when:** `MonteCarloEngine` runs through `IComputeBackend` with no measurable regression on the CPU path.
- [ ] Extract `IComputeBackend` interface with the `RunSimulationsAsync` signature above.
- [ ] Implement `CpuComputeBackend` wrapping the existing `Parallel.For` + `SimulateOnce` logic.
- [ ] Register `IComputeBackend` in `App.axaml.cs` -> `ConfigureServices` (singleton, `CpuComputeBackend` initially).
- [ ] Update `MonteCarloEngine` constructor to accept `IComputeBackend` via DI.
- [ ] Verify every existing caller (`OptimizationRunner`, `MonteCarloViewModel`, any test harness) still resolves the engine through DI with no caller-side change.
- [ ] Baseline: CPU wall time for a known grid unchanged within +/- 5%.

### Phase 27B -- GPU kernel implementation
- **Size:** L · **Depends on:** 27A · **Succeeds when:** `GpuComputeBackend` produces statistically identical results to `CpuComputeBackend` on the same input (P(ruin), P(funded), terminal-balance percentiles match to 3 sig figs at 10k iterations).
- [ ] Add `ComputeSharp` PackageReference to `backs2.csproj`.
- [ ] Implement `GpuComputeBackend` in `src/core/Overview/GpuComputeBackend.cs`.
- [ ] Port `SimulateOnce` to a `[Shader]`-attributed compute kernel reproducing the exact state machine: MTM-low vs daily-floor / max-DD floor check, then realized PnL, then HWM update, then EOD reset.
- [ ] Per-iteration RNG must be seed-split the same way the CPU path does it (`new Random(seed + i)`) so determinism is preserved when `seed` is supplied.
- [ ] Chunk iterations under TDR limits: default batch of ~10k iterations per dispatch, configurable via `JobConfig`.
- [ ] Respect `MaxIterations` (100,000) and `MaxCurveCells` (100,000,000) at the backend boundary -- the ceilings apply regardless of backend.
- [ ] Fallback: `GpuComputeBackend` constructor probes for a working device; on failure it stores a `CpuComputeBackend` fallback and logs a Warn. `RunSimulationsAsync` delegates rather than throwing.
- [ ] Invariants baked into the kernel: Monte Carlo operates in the time domain only. Never percentage-compound. Never scramble trades.

### Phase 27C -- Worker IPC integration
- **Size:** M · **Depends on:** 27B · **Succeeds when:** the Avalonia UI parent can request a GPU-accelerated MC run and receive aggregated stats back; the worker holds the GPU; the parent-to-worker payload stays under the 64 MB `WorkerIpc` frame cap.
- [ ] Extend `WorkerEnvelope` with a `bool RequiresGpu` field.
- [ ] Extend `JobConfig` with `int GpuDeviceIndex` (default -1 = any available) and `int GpuIterationBatch` (default 10,000).
- [ ] Update `WorkerHost` to resolve `IComputeBackend` from the job config: `RequiresGpu = true` -> try `GpuComputeBackend`, fall back to `CpuComputeBackend` on any initialization failure.
- [ ] Worker logs the chosen backend and (on GPU) the adapter name at Debug level.
- [ ] Parent (`ComputeWorkerManager`) surfaces the backend choice in the progress stream so the UI status line can show "GPU: <adapter>" or "CPU (GPU unavailable)".
- [ ] Do NOT ship raw per-iteration data across IPC. The worker runs the full MC internally and returns aggregated `MonteCarloResult` scalars plus optional `TerminalBalances[]` (100k doubles = 800 KB, well under the cap).

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

### Phase 22E -- Runtime diagnostic baseline [IN PROGRESS]

Captures CPU / GC / allocation numbers on the current build so Phase 23's impact on runtime is provable, not just its impact on build time. No code changes -- measurements only.

Tooling: `dotnet-counters` and `dotnet-trace` (free; no dotTrace / PerfView dependency).

- [ ] Install: `dotnet tool install -g dotnet-counters` and `dotnet tool install -g dotnet-trace`.
- [ ] Cold-start baseline: capture `[startup]` Stopwatch markers from the freshest `logs/runtime-*.log`.
- [ ] Cache-load baseline: 600k-bar read time from `Cache: loaded ...` log line.
- [ ] MC baseline: `dotnet-counters monitor -n backs2 --counters System.Runtime` during a single Optimize run at default settings.
- [ ] Trace baseline: `dotnet-trace collect -n backs2 --duration 00:00:30` during the same run, then `dotnet-trace report <trace> topN -n 20`.
- [ ] Record the outputs below. These are the numbers Phase 23 is compared against.

**Baseline (2026-10-09):**

Startup path (from `logs/runtime-*.log`, Phase 17 markers):
- DI container: 25 ms
- ConfigManager: 48 ms
- MT5 server: 6 ms
- MainViewModel: 22 ms
- MainWindow constructed: 230 ms
- **OnFrameworkInitializationCompleted total: 348 ms**

Runtime footprint (from `dotnet-counters collect --counters System.Runtime`, `logs/counters.csv`):

| Scenario | Working Set | CPU |
|---|---|---|
| Idle, post-startup (all 12 tab views pre-warmed) | ~1500 MB | <1% |
| **During Optimize** | min 472 MB / max 2987 MB / avg 1338 MB | **max 76.5% / avg 22.4%** |

**Key finding — Optimize is not CPU-bound.** On a 20-core machine, a saturated `Parallel.ForEachAsync` would pin CPU near 100%. The capture shows 22% average and 76% peak. The optimizer is spending most of its wall time waiting — on disk I/O, on `StrategyManager`'s compile cache, on MC curve allocation, or on a serialized stage. Phase 23 is the wrong tool for this; it needs its own investigation.

**Follow-up:**
- [ ] Locate the serialized stage in `OptimizationRunner.RunAsync` — the coarse MC loop and the refine loop are both sequential; only the per-combination backtest is parallel. That's likely where the wall time goes.
- [ ] Capture during a 100+ version grid where the parallel phase dominates.
- [ ] Compare CPU during the backtest phase vs. the MC phase, at least two separate captures with a marker in the log.
- [ ] 600k-bar cache load timing: still not captured — trigger explicitly via a Chart tab load during a counters session.
- [ ] **GUI lag during optimization runs** (reported 2026-10-10). The main UI thread stutters while Optimize runs. Suspects, in order: (a) `Progress<OptimizationProgress>` fires 2000+ times per run and marshals each report through `SynchronizationContext` to the UI dispatcher, saturating it; (b) the status-text binding on every tick triggers a layout pass; (c) `IProgress` closures allocate per call. Fix direction: coalesce progress to ~10 Hz at the runner (not at the UI), and rate-limit the dispatcher post for status updates.

Captured via: `dotnet-counters collect -p <pid> --counters System.Runtime --output logs/counters.csv --format csv`. Raw CSV in `logs/` (git-ignored).

**Warning - Metreja profiler overhead is not neutral.** Running the optimizer under Metreja v1.0.23 slows each per-dataset Optimize run by roughly 24x (measured 2026-10-10: ~10 s baseline per dataset becomes ~4 min under profiler; a full multi-dataset batch that normally completes in ~1 min stretched to ~1.5 hours). The parallel MC topology is the cause: 20 concurrent worker threads each push enter/leave or method_stats events into the profiler's shared writer, which serializes on a buffer lock. Consequences: (a) wall-time and CPU% under the profiler are not comparable to unprofiled numbers; (b) Metreja is a hotspot *attribution* tool, not a perf-measurement tool. For perf measurement, use `dotnet-counters` or the in-app `[startup]` Stopwatch markers. Narrow the include to one namespace and use `method_stats` (not `enter leave`) to keep the trace under MB scale and the overhead under 2x.
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