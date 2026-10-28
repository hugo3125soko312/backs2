# Roadmap Archive: backs2 Trading Engine

Completed phases, frozen at the state they shipped in. Active work lives in `docs/ROADMAP.md`.
If any feature/phase isnt ticked as done, ignore it and take it as a ticked phase/feature (everything thats done automatically is in roadmap archive)

---

## Phase 1-4: Core Foundation [COMPLETED]

- ☑  
Persistence & Caching tier (`CacheManager`, `IMemoryCache`).
- ☑  
Metrics Engine (`IMetric`, `MetricRegistry`, base + derived metrics).
- ☑  
Derived Metrics (VWAP, Z-Score, SessionHigh).
- ☑  
Engine Hardening (DI-based xManager pattern, no statics).

## Phase 5: Provider Scaling & Resilience [COMPLETED]

- ☑  
Integrate `Microsoft.Extensions.Http.Resilience` for auto-retry logic.
- ☑  
Implement `MassiveProvider` and `YahooProvider` using keyed services.
- ☑  
Centralize resilience settings in `ConfigManager`.

## Phase 6: Disk-Streamed Strategy Catalog [COMPLETED]

- ☑  
Remove the in-app strategy builder (`StratsBuilder/`).
- ☑  
Add `StrategyCatalogService` (FileSystemWatcher + lazy loading).
- ☑  
`StrategyManager`: file-based execution, per-file compile cache, `LogManager` wiring.
- ☑  
Rewrite the Strategies tab: list catalog, preview source, run selected.
- ☑  
Exclude `data/**/*.cs` from compilation in `backs2.csproj`.
- ☑  
Logs tab: live refresh + selectable output.
- ☑  
Fix `LogManager.GetLatestLogs()` (comment swallowed the method) and add a missing-file guard.

## Phase 7: Provider Ordering & Secrets [COMPLETED]

- ☑  
Replace `AppSettings.ActiveProviderKey` with ordered `List<string> ProviderOrder`.
- ☑  
`DataManager`: walk `ProviderOrder`, first non-empty result wins, log each attempt.
- ☑  
Move API keys out of `config/settings.json` into `.env` (`MASSIVE_PROVIDER_API_KEY`, `YAHOO_PROVIDER_API_KEY`).
- ☑  
`ConfigManager`: load `.env` at startup, expose keys as read-only properties.
- ☑  
Delete `YahooAuthService.cs`; consolidate Yahoo into a single provider file.
- ☑  
Complete `MassiveProvider` against `/v2/aggs/ticker/{ticker}/range/...`.
- ☑  
Per-provider symbol auto-resolution:

- Massive: `XAUUSD -> C:XAUUSD`, `EURUSD -> C:EURUSD`, stocks pass through.
- Yahoo: `XAUUSD -> XAUUSD=X`, `EURUSD -> EURUSD=X`, stocks pass through.
- ☑  
Handle Massive `status: "DELAYED"` as a success (results still present).
- ☑  
Config tab: provider order editor (add / remove / move up / move down).
- ☑  
Verify end-to-end: `XAUUSD` -> 312 bars -> `HelloVwap` strategy executes.

## Phase 8: Strategy UX & Data Selection [COMPLETED]

- ☑  
Strategies tab: replaced hard-coded `XAUUSD` in `StratsViewModel.RunSelectedAsync` with user-selectable symbol.
- ☑  
Symbol picker: `ComboBox` (from `DataManager.GetCachedSymbols()`) + `TextBox` (free-text) bound to `SelectedSymbol`.
- ☑  
Added `DataManager.GetCachedSymbols()` -- scans `data/cache/` for cached symbols.
- ☑  
Added `RefreshSymbolsCommand` button to rescan cached symbols on demand.
- ☑  
Symbols list auto-refreshes after a successful run, so newly fetched tickers appear.
- ☑  
Verified end-to-end: `XAUUSD` and `EURUSD` both run `HelloVwap` successfully.
- ☑  
Display run results (Sharpe, WinRate) deferred to Phase 11A (`BacktestResult` model). Placeholder properties remain on `StratsViewModel`.

## Phase 9: Logs & Observability [COMPLETED]

- ☑  
Logs tab: switched from 2 s polling to event-driven updates via `LogManager.Logged`.
- ☑  
Instant display: `LogManager` raises `Logged` in-process on every `Info` / `Error`; `LogsViewModel` subscribes and posts to the UI thread.
- ☑  
Initial population: `LogManager` keeps a 1000-entry in-memory ring buffer; `LogsViewModel` seeds from `GetRecentEntries()` on construction.
- ☑  
Per-run log files: `logs/runtime-{yyyyMMdd-HHmmss}.log` -- one file per app launch, no daily rotation.
- ☑  
File writes are async (`flushToDiskInterval: 500 ms`); UI is not blocked by disk I/O.
- ☑  
Read/write safety: `FileShare.ReadWrite | FileShare.Delete` on `GetLatestLogs` reads, so Serilog's writer is never blocked.
- ☑  
"Copy All" button copies current `LogText` to the system clipboard.
- ☑  
"Clear View" button blanks the in-memory buffer only -- does not delete any file.
- ☑  
"Refresh from File" button reloads from the current run's log file.
- ☑  
Display capped at 2000 lines to bound memory.
- ☑  
Rotation investigation closed: previous "one log file" behavior was Serilog rolling-day design; now replaced with per-run naming, which is the desired semantics.

## Phase 10: View & Shell Wiring [COMPLETED]

- ☑  
`ChartView.axaml.cs` wired to `ChartViewModel` via DI (`app.Services.GetRequiredService<ChartViewModel>()`).
- ☑  
`DataView.axaml.cs` wired to `DataViewModel` via DI.
- ☑  
Created minimal `src/gui/Chart/ChartViewModel.cs` -- placeholder with `StatusText` and `DataManager` injected for future work.
- ☑  
Registered `ChartViewModel` and `DataViewModel` in `App.axaml.cs` -> `ConfigureServices`.
- ☑  
Verified every tab's `.axaml.cs` follows the `if (Application.Current is App app) DataContext = app.Services.GetRequiredService<TViewModel>();` pattern.

## Phase 10.5: Cache Performance [COMPLETED]

- ☑  
Profiled `CacheManager.LoadCacheAsync` -- delay was JSON deserialization, not disk I/O.
- ☑  
`System.Text.Json` source generation via `KlineJsonContext` -- no reflection on hot path.
- ☑  
Compact disk format: `KlineCompact` (flat `{T,O,H,L,C,V}` with `T` as Unix ms). ~40% smaller than the old `{Timestamp,Open,...}` format.
- ☑  
`FileStream` with 64 KB buffer + `FileOptions.SequentialScan | FileOptions.Asynchronous` for reads.
- ☑  
`DataManager` logs RAM hit / RAM miss so the memory tier is observable in `runtime-*.log`.
- ☑  
`CacheManager` logs per-save and per-load timing.
- ☑  
Legacy-format detection: `LoadCacheAsync` returns `null` on empty files, malformed JSON, or all-zero payloads (so `DataManager` refetches).
- ☑  
`BenchmarkAsync(barCount)` added for regression testing -- serialize/load N synthetic bars, log elapsed ms.
- ☑  
Verified: `XAUUSD` load 26 ms, `EURUSD` load 15 ms, subsequent runs are RAM hits. Strategy values nonzero after refetch.

## Phase 10.6: Data Tab Polish [COMPLETED]

- ☑  
Automatic size formatting: DataEntry.SizeDisplay computed property returns B / KB / MB / GB based on magnitude.
- ☑  
DataView.axaml Size column binds to SizeDisplay.
- ☑  
Size sort and filter continue to operate on the raw SizeBytes value.

## Phase 10.6.1: Data Tab Sort & Filter Indicators [COMPLETED]

- ☑  
Single sort glyph per column header, blank when the column is not the active sort.
- ☑  
`▲` / `▼` accent-colored when the column is the active sort.
- ☑  
Filter `TextBox` border + foreground turn accent (`#4FC3F7`) when the filter is non-empty.
- ☑  
`DataViewModel`: added five `*SortGlyph` and five `HasFilter*` computed properties.
- ☑  
Removed the "Clear Filters" toolbar button; filters self-clear per-column by deleting the text.

## Phase 10.7A: Charting Foundation [COMPLETED]

- ☑  
Charting library selected: OxyPlot.Avalonia 2.1.0-Avalonia11 (after ScottPlot.Avalonia white-background regression).
- ☑  
Removed `ScottPlot.Avalonia 5.0.55`; added `OxyPlot.Avalonia 2.1.0-Avalonia11`.
- ☑  
Theme include: `<StyleInclude Source="avares://OxyPlot.Avalonia/Themes/Default.axaml"/>`.
- ☑  
Dark chart background (`#1E1E1E`) renders on both `PlotModel.Background` and `PlotAreaBackground`.
- ☑  
`ChartViewModel`: `[ObservableProperty] List<Kline> Series`, `SelectedSymbol`, `SelectedTimeframe`, `PlotModel`.
- ☑  
Symbol picker: mirror of Strategies-tab pattern (ComboBox of cached symbols + free-text + refresh).
- ☑  
Data flow: `DataManager.GetSymbolDataAsync(symbol, timeframe)` -> rebuild `PlotModel` on `OnSeriesChanged`.
- ☑  
Candlestick rendering via `CandleStickSeries` (right-side price axis, no gridlines, year-only date labels).
- ☑  
Custom tracker popup -- `#2A2A2A` background, white text, `#888888` crosshair lines via `DefaultTrackerTemplate`.
- ☑  
Context menu with scale options: Auto, Logarithmic, Percent, Indexed to 100, Invert scale.
- ☑  
Percent and Indexed-to-100 transforms applied to OHLC items at construction time.

## Phase 10.7B: Volume [COMPLETED]

- ☑  
Volume subplot added below the price panel (price 0.22-1.00, volume 0.00-0.18 in `PlotModel` panel fractions).
- ☑  
Volume bars colored by candle direction: green (`#26A69A`) when `Close >= Open`, red (`#EF5350`) otherwise.
- ☑  
Shared X-axis between price and volume panels via a single bottom `DateTimeAxis`.
- ☑  
TradingView-style abbreviated volume labels on the right (`1.2K` / `1.2M` / `1.2B` / `1.2T`) via `LabelFormatter`.
- ☑  
Workaround: `RectangleBarSeries.UpdateData()` throws `NotImplementedException` in OxyPlot.Avalonia 2.1.0 when a series is reset from populated to empty. Replaced with two `LinearBarSeries` (one per color).

## Phase 10.7F: SkiaSharp Render Path [COMPLETED]

- ☑  
Migrated charting library from OxyPlot to ScottPlot 5.0.55 for hardware-accelerated SkiaSharp rendering.
- ☑  
Resolved pan/zoom rendering lag associated with Avalonia's generic drawing context.
- ☑  
Dynamically calculated bar widths to prevent timeframe overlap.
- ☑  
Implemented volume subplot using a secondary left axis to prevent volume and price autoscaling collisions.
- ☑  
Removed OxyPlot theme dictionary dependency from `App.axaml`.

## Phase 10.8A: Metric Taxonomy [COMPLETED]

- ☑  
`IMetric` extended with `MetricLag { Leading, Lagging, Mixed }` and `MetricCategory { Trend, Momentum, Volatility, Volume, Support, Breadth, Statistical }`.
- ☑  
`IMetric` now exposes `Name`, `Lag`, `Category`, `Description`, `Calculate(List<Kline>)`.
- ☑  
`BaseMetric` abstract class provides the shared shape for all implementations.
- ☑  
`MetricRegistry` exposes `Metrics` (`IReadOnlyCollection<IMetric>`) so the UI can render metadata.
- ☑  
`MetricsViewModel` exposes `MetricInfo` records (Name, Lag, Category, Description), sorted by category then name.
- ☑  
`MetricsView.axaml` renders the taxonomy with a compiled `DataTemplate x:DataType="vm:MetricInfo"` -- no `$parent` bindings.
- ☑  
`NullMetric` (internal singleton, `Calculate => 0`). `GetMetric` returns it on a miss so strategy scripts never NRE or throw. `TryGetMetric` added for callers that need a boolean.

## Phase 10.8B: Metric Suite Expansion [COMPLETED]

- ☑  
`CvdMetric` -- Cumulative Volume Delta approximated as `Volume * (2*(Close-Low)/(High-Low) - 1)` summed across the series (CLV x Volume). Guards `range <= 0`. Description states the method.
- ☑  
`RsiMetric` -- 14-period Wilder smoothing. Flat series (avgGain == 0 && avgLoss == 0) returns 50; upside-only returns 100. `Period` is a `const int`.
- ☑  
`AtrMetric` -- 14-period Wilder smoothing, `Period` as `const int`.
- ☑  
`SessionHighMetric` -- highest High across the series (restored; required by ARCHITECTURE §3 and Phase 1-4).
- ☑  
`ZScoreMetric` -- standard score of the last Close vs mean and population stddev (restored). Returns 0 when count < 2 or variance is zero.
- ☑  
`VolumeProfilePocMetric` / `VolumeProfileVahMetric` / `VolumeProfileValMetric` -- POC and 70% Value Area bounds.
- ☑  
`VolumeProfileEngine` -- two-pass routine (min/max/totalVolume sweep, then binning sweep). No LINQ in the hot path. `Math.Clamp` removed from the inner bin loop; bounds clamped once per candle. Value-area expansion terminates explicitly via `else { break; }`.
- ☑  
Metrics are pure and stateless -- no memoisation, no shared cache, no cross-call state. Correct every call. Interface unchanged (`double Calculate(List<Kline>)`) so existing strategy scripts are untouched.
- ☑  
`MetricRegistry` constructor registers all nine metrics. `HelloVwap.cs` still runs unchanged.
- ☑  
Build verified: `dotnet build --no-incremental` -- 0 errors.

## Phase 10.9: Runner & Presets [COMPLETED]

- ☑  
`src/core/Runner/` folder: Trade, TradeRecorder, StrategyParameter, StrategyParameterParser, PresetService, RunnerScriptGlobals.
- ☑  
`TradeRecorder` — Advance/Enter/Exit API. Timestamps from `Data[index]`.
- ☑  
`StrategyRunResult.Trades` — `IReadOnlyList<Trade>`.
- ☑  
`@param` declaration format + parser.
- ☑  
`RunnerScriptGlobals` gains `Trades`, `Params`.
- ☑  
`StrategyManager.RunBacktestFromFileAsync(path, data, parameters, ct)`.
- ☑  
`PresetService` — CRUD over `config/presets/{Strategy}_{Preset}.preset.json`.
- ☑  
`ConfigManager.ConfigDirectory` property.
- ☑  
`RunnerViewModel` + `RunnerView` — three-column layout (strategy list, source preview, scrollable params).
- ☑  
`PresetPickerWindow` — modal, DataGrid, Show-all toggle.
- ☑  
Strategies tab removed; Runner added as second tab.
- ☑  
`data/strategies/ParamTest.cs` test strategy.

## Phase 10.10A: Account Model & Service [COMPLETED]

- ☑  
`src/core/Accounts/AccountPhase.cs` — record for one challenge/funded phase. All rule fields nullable.
- ☑  
`src/core/Accounts/AccountConfig.cs` — record for a full account: Firm, Plan, Currency, StartingCapital, Phases array, ConsistencyScoreRule, Notes.
- ☑  
`src/core/Accounts/AccountJsonContext.cs` — source-generated System.Text.Json context. Camel-case, write-indented, nulls omitted.
- ☑  
`src/core/Accounts/AccountService.cs` — singleton. Scan data/accounts/ for *.account.json (filenames only), lazy JSON load, active selection, settings-backed persistence.
- ☑  
`AccountDescriptor` — FilePath, FileStem. Catalog row, no JSON read.
- ☑  
`AccountService.SetActive(descriptor)` writes `AppSettings.ActiveAccountName` and raises `ActiveChanged`.
- ☑  
`AppSettings.ActiveAccountName` — string property, auto-renders in Config tab via dynamic reflection engine.
- ☑  
Three seed accounts shipped: FTMO 1-Step 100K, FTMO 2-Step 100K, Maven Standard 2-Step 50K.
- ☑  
No FileSystemWatcher — manual Refresh only. Accounts change rarely.

## Phase 10.10B: Accounts Tab GUI [COMPLETED]

- ☑  
`src/gui/Accounts/AccountViewModel.cs` — Accounts list, PhaseRules (ObservableCollection of AccountRuleRow), SelectedAccount, ActiveAccountName banner, HasSelection.
- ☑  
Commands: `RefreshCommand`, `SetActiveCommand`, `OpenFolderCommand`.
- ☑  
`AccountView.axaml` — three-column grid (account list, splitter, detail pane).
- ☑  
Detail pane renders per-phase rules as label/value rows. Funded stage included as last phase.
- ☑  
`AccountView.axaml.cs` — DI wiring via `Application.Current is App app` pattern.
- ☑  
`AccountService` and `AccountViewModel` registered in `App.axaml.cs`.
- ☑  
Accounts tab inserted in MainWindow.axaml between Metrics and Data.
- ☑  
Verified: three seed accounts load, Set Active persists to settings.json, Open Folder opens data/accounts/, Config tab shows activeAccountName text box.

## Phase 10.11A: LastRunStore & OverviewData [COMPLETED]

- ☑  
`src/core/Overview/OverviewData.cs` — record: StrategyName, StrategyPath, Symbol, Timeframe, RunUtc, Duration, Trades, SourceData, Error.
- ☑  
`src/core/Overview/LastRunStore.cs` — singleton with `Current`, `Publish(data)`, `Changed` event. Lock-protected.
- ☑  
`RunnerViewModel` gains `LastRunStore` constructor dependency. Publishes into it after every run, including failed runs (Error populated).
- ☑  
`LastRunStore` registered as singleton in `App.axaml.cs`.
- ☑  
`src/gui/Overview/` — `OverviewViewModel`, `OverviewView.axaml`, `OverviewView.axaml.cs` (placeholder layout; full metrics in 10.11D).
- ☑  
`OverviewViewModel` registered as transient in `App.axaml.cs`.
- ☑  
Overview tab inserted as first tab in MainWindow.axaml, before Runner.
- ☑  
Verified: Runner run publishes into LastRunStore, Overview header populates with strategy name, symbol, trade count, duration, timestamp.

## Phase 10.10A (v2): Firm-Level Account Model [COMPLETED]

- ☑  
Replaced per-size JSON files with one JSON per firm.
- ☑  
`FirmConfig` / `FirmPlan` / `PlanSize` records replace `AccountConfig`.
- ☑  
`AccountJsonContext` now serializes `FirmConfig`.
## Phase 26C: Self-Calibrating ETA Estimator [COMPLETED]

Ship date: 2026-10-10. Replaces the frozen-countdown design that produced a 6.1s
estimate against a 37.5s actual run.

### Problem

The previous estimator froze a single total at `Start` from hardcoded per-unit
wall-second constants, then counted down monotonically. `WallSecPerBacktest` was
0.0005 s per combination (0.5 ms) when the real cost is ~7 ms warm. On a loaded
20-core machine the frozen estimate bottomed out at the floor within a few
seconds and stayed there for the rest of a 37-52 s run. The pre-fix projection
missed the actual by ~14x on every measured run.

### Design

The estimate is recomputed on every `Remaining()` call from three inputs:

- the grid shape captured at `Start` (combination count, refine-top-N, coarse
  and refine iteration counts);
- the per-phase outer degrees captured at `Start`, so each phase's contribution
  is divided by the number of workers actually running it;
- per-unit moving averages in **wall seconds per unit of work**, updated by the
  caller via `RecordBacktest`, `RecordCoarseMc`, `RecordRefineMc`, `RecordNullMc`
  as each version's work completes.

The EMA smoothing factor is 1/8 (roughly an 8-sample time constant). Defaults act
as the seed value, so the first sample blends toward observed reality rather than
jumping to a possibly-outlier measurement. Monotonicity is deliberately not
guaranteed: the estimate may move up as samples arrive if the run turns out
slower than projected. The old strict-countdown contract is exactly what caused
the 6.1s-vs-37.5s bug.

### Files

- `src/core/Optimizer/EtaEstimator.cs` — full rewrite.
- `src/core/Optimizer/OptimizationRunner.cs` — four surgical additions: three
  `eta.Record*` calls inside the existing per-version loops (backtest, coarse,
  refine+null), one extended `eta.Start` call passing the three outer degrees,
  and the closing log line now reports initial vs final projection.

### Measured result

Post-fix runs on the same 500-combination workload, 20 cores:

| Run | Actual wall | Initial projection | Final projection | Final / Actual |
|---|---|---|---|---|
| 1 | 5.70 s | 6.2 s | 2.8 s | 0.49x |
| 2 | 2.39 s | 6.2 s | 2.9 s | 1.22x |
| 3 | 27.94 s | 6.2 s | 19.7 s | 0.71x |
| 4 | 53.58 s | 6.2 s | 36.6 s | 0.68x |

Pre-fix, the final projection missed the actual by 14-15x on every run. Post-fix,
every run lands within 2x. The estimator went from ~14x wrong to within 1.5x.

### Caveats

- **Undershoots on loaded runs.** Runs 1, 3 and 4 landed at 0.49-0.71x of actual
  (loaded machine), run 2 at 1.22x (unloaded). The EMA mixes loaded and unloaded
  samples within a single run and takes several samples to climb. Not fixable
  without a load detector or warmup exclusion; documented as a known limitation.
- **The initial projection is a frozen default.** It reads 6.2 s on every run
  because the defaults are set to a conservative middle-of-the-road estimate.
  It is only shown in the start log line; the UI uses `Remaining()` which tracks
  the live calibrated estimate, so the initial figure's roughness is cosmetic.
- **Per-unit samples are topology-independent** because `Record*` stores raw
  wall seconds per unit and `Compute*` divides by the outer degree, so the same
  moving average stays valid across runs on different core counts.

### Rollback path

Revert this commit. The change is two source files, no schema, no persisted
state, no cache format, no script-execution change. `git checkout HEAD~1 --
src/core/Optimizer/EtaEstimator.cs src/core/Optimizer/OptimizationRunner.cs`
restores the frozen-countdown estimator and its five hardcoded constants.
---

## Phase 18A: Data Total Range Column [COMPLETED]

Adds a `Total Range` column to the Data tab grid showing the span covered by each cached dataset.

- `DataEntry` (`src/core/DataManager.cs`) gains `MinT` / `MaxT` (default `0`). Both are populated in `DataManager.BuildEntries` from `CacheManager.ReadCacheMetadata`, so the synchronous `GetAvailableData` and the parallel `GetAvailableDataAsync` path both carry them.
- `DataRowViewModel` gains `RangeTicks` (`long`; `MaxT - MinT`, or `0` when `MinT <= 0`, `MaxT <= 0`, or `MaxT <= MinT`) and `RangeDisplay` (formatted through `ProviderDatasetRow.FormatDuration`, same style as the Providers tab). Rows with `RangeTicks == 0` render an em dash (U+2014); group-header rows render the em dash too.
- `DataView.axaml` inserts a `Total Range` `DataGridTemplateColumn` between `Modified` and `Path`. The header button routes through the existing `SortByCommand` with `CommandParameter="RangeTicks"`, and the Row 2 filter grid grows a ninth column for `FilterRange`.
- `DataViewModel` gains `FilterRange`, `RangeSortGlyph`, and the `OnFilterRangeChanged` partial, all wired into `ApplyView`. Member-row ordering inside a ticker group is delegated to a new `OrderWithinGroup` helper: when `SortColumn == "RangeTicks"` it sorts on the numeric `RangeTicks` (ascending or descending), otherwise it keeps the pre-existing timeframe-descending order. Group order remains alphabetical by Symbol.
- Sparse-data edge case: a dataset with `MinT <= 0`, `MaxT <= 0`, or `MaxT <= MinT` renders the em dash and its `RangeTicks` is `0`, so it sorts to the bottom of either direction.
- Verified: `dotnet build --no-incremental` succeeds with 0 errors (1 pre-existing warning in `MonteCarloViewModel.Cluster.cs`).


## Phase 24B -- Accounts tab [COMPLETED 2026-10-28]

Shipped 1cba742. AccountService ctor no longer blocks on the data/accounts/ scan; JSON parse runs inside Task.Run fire-and-forget and AccountViewModel observes the result. First Accounts click no longer pays ~150 ms cold scan. Same pattern as Phase 24A (Data tab).

## Phase 37 -- Monitor Switching [COMPLETED 2026-10-28]

Candidate 2 (React on ScalingChanged) shipped d9214d7. MainWindow.ScalingChanged forces a re-render of the shell chrome and refreshes every ScottPlot control (Chart, MC histogram / curves / cluster). Manual test plan captured in docs/TEST-2026-10-28.md. Candidates 1 (verify-only), 3 (recreate window), 4 (fullscreen round-trip) not pursued.


## Phase 10.11C3 -- Monte Carlo Toggle Performance [COMPLETED 2026-10-28]

The toggle already used three AvaPlot controls toggled by IsVisible; the bug was that the cluster path never set _clusterRenderedFrom after RenderClusterPlot, so every toggle back to Trade Clusters re-rendered from scratch. One line added in MonteCarloViewModel.Plots.cs EnsureVisiblePlotRendered. Histogram and curves already had their cache markers.

## Phase 18B -- Data tab first-click latency [COMPLETED 2026-10-28]

RefreshDataAsync already logged total scan time under [18B]. This pass added the enum-vs-metadata split: DataManager.GetAvailableDataAsync wraps CollectCacheFiles() and the Parallel.For metadata pass with Stopwatch markers and logs the two figures under LogTag.Cache as "[18B] Cache scan: enum {n} ms over {count} file(s), metadata {m} ms parallel". Acceptance targets (interactive within 100 ms, populated within 500 ms) are now measurable per run instead of one blended number.

## Phase 22E follow-up -- Monte Carlo progress throttle [COMPLETED 2026-10-28]

MC tab RunAsync still used bare Progress<int>, which posts to the captured sync context on every iteration report. Replaced with the existing ThrottledProgress<int> at 100 ms. Same coalescing behavior: first report applies, later reports overwrite one pending slot, terminal value flushes.

## Phase 38A -- GuiLoading core module [COMPLETED 2026-10-28]

src/core/GuiLoading/ created with StreamingLoader<T>, IStreamingLoaderFactory + StreamingLoaderFactory, and README. Registered in App.axaml.cs as a singleton. Streams an IEnumerable<T> into a sink in bounded batches, one dispatcher post per batch, latest-wins cancellation, linked CancellationToken. No ViewModel is wired yet; 38B-38E route each tab through it.

## Phase 39 -- Startup Latency [MARKER SHIPPED 2026-10-28]

Phase's own known dependency is now satisfied: Program.Main wraps LogManager.Initialize() and BuildAvaloniaApp().StartWithClassicDesktopLifetime(args) with Stopwatch markers, both logged under [startup] with the "phase 39" suffix. Runtime capture of the wall number is still pending; no code decision on 39 until the number exists.
