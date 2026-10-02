# Roadmap Archive: backs2 Trading Engine

Completed phases, frozen at the state they shipped in. Active work lives in `docs/ROADMAP.md`.
If any feature/phase isnt ticked as done, ignore it and take it as a ticked phase/feature (everything thats done automatically is in roadmap archive)
---

## Phase 1-4: Core Foundation [COMPLETED]
- [x] Persistence & Caching tier (`CacheManager`, `IMemoryCache`).
- [x] Metrics Engine (`IMetric`, `MetricRegistry`, base + derived metrics).
- [x] Derived Metrics (VWAP, Z-Score, SessionHigh).
- [x] Engine Hardening (DI-based xManager pattern, no statics).

## Phase 5: Provider Scaling & Resilience [COMPLETED]
- [x] Integrate `Microsoft.Extensions.Http.Resilience` for auto-retry logic.
- [x] Implement `MassiveProvider` and `YahooProvider` using keyed services.
- [x] Centralize resilience settings in `ConfigManager`.

## Phase 6: Disk-Streamed Strategy Catalog [COMPLETED]
- [x] Remove the in-app strategy builder (`StratsBuilder/`).
- [x] Add `StrategyCatalogService` (FileSystemWatcher + lazy loading).
- [x] `StrategyManager`: file-based execution, per-file compile cache, `LogManager` wiring.
- [x] Rewrite the Strategies tab: list catalog, preview source, run selected.
- [x] Exclude `data/**/*.cs` from compilation in `backs2.csproj`.
- [x] Logs tab: live refresh + selectable output.
- [x] Fix `LogManager.GetLatestLogs()` (comment swallowed the method) and add a missing-file guard.

## Phase 7: Provider Ordering & Secrets [COMPLETED]
- [x] Replace `AppSettings.ActiveProviderKey` with ordered `List<string> ProviderOrder`.
- [x] `DataManager`: walk `ProviderOrder`, first non-empty result wins, log each attempt.
- [x] Move API keys out of `config/settings.json` into `.env` (`MASSIVE_PROVIDER_API_KEY`, `YAHOO_PROVIDER_API_KEY`).
- [x] `ConfigManager`: load `.env` at startup, expose keys as read-only properties.
- [x] Delete `YahooAuthService.cs`; consolidate Yahoo into a single provider file.
- [x] Complete `MassiveProvider` against `/v2/aggs/ticker/{ticker}/range/...`.
- [x] Per-provider symbol auto-resolution:
  - Massive: `XAUUSD -> C:XAUUSD`, `EURUSD -> C:EURUSD`, stocks pass through.
  - Yahoo: `XAUUSD -> XAUUSD=X`, `EURUSD -> EURUSD=X`, stocks pass through.
- [x] Handle Massive `status: "DELAYED"` as a success (results still present).
- [x] Config tab: provider order editor (add / remove / move up / move down).
- [x] Verify end-to-end: `XAUUSD` -> 312 bars -> `HelloVwap` strategy executes.

## Phase 8: Strategy UX & Data Selection [COMPLETED]
- [x] Strategies tab: replaced hard-coded `XAUUSD` in `StratsViewModel.RunSelectedAsync` with user-selectable symbol.
- [x] Symbol picker: `ComboBox` (from `DataManager.GetCachedSymbols()`) + `TextBox` (free-text) bound to `SelectedSymbol`.
- [x] Added `DataManager.GetCachedSymbols()` -- scans `data/cache/` for cached symbols.
- [x] Added `RefreshSymbolsCommand` button to rescan cached symbols on demand.
- [x] Symbols list auto-refreshes after a successful run, so newly fetched tickers appear.
- [x] Verified end-to-end: `XAUUSD` and `EURUSD` both run `HelloVwap` successfully.
- [x] Display run results (Sharpe, WinRate) deferred to Phase 11A (`BacktestResult` model). Placeholder properties remain on `StratsViewModel`.

## Phase 9: Logs & Observability [COMPLETED]
- [x] Logs tab: switched from 2 s polling to event-driven updates via `LogManager.Logged`.
- [x] Instant display: `LogManager` raises `Logged` in-process on every `Info` / `Error`; `LogsViewModel` subscribes and posts to the UI thread.
- [x] Initial population: `LogManager` keeps a 1000-entry in-memory ring buffer; `LogsViewModel` seeds from `GetRecentEntries()` on construction.
- [x] Per-run log files: `logs/runtime-{yyyyMMdd-HHmmss}.log` -- one file per app launch, no daily rotation.
- [x] File writes are async (`flushToDiskInterval: 500 ms`); UI is not blocked by disk I/O.
- [x] Read/write safety: `FileShare.ReadWrite | FileShare.Delete` on `GetLatestLogs` reads, so Serilog's writer is never blocked.
- [x] "Copy All" button copies current `LogText` to the system clipboard.
- [x] "Clear View" button blanks the in-memory buffer only -- does not delete any file.
- [x] "Refresh from File" button reloads from the current run's log file.
- [x] Display capped at 2000 lines to bound memory.
- [x] Rotation investigation closed: previous "one log file" behavior was Serilog rolling-day design; now replaced with per-run naming, which is the desired semantics.

## Phase 10: View & Shell Wiring [COMPLETED]
- [x] `ChartView.axaml.cs` wired to `ChartViewModel` via DI (`app.Services.GetRequiredService<ChartViewModel>()`).
- [x] `DataView.axaml.cs` wired to `DataViewModel` via DI.
- [x] Created minimal `src/gui/Chart/ChartViewModel.cs` -- placeholder with `StatusText` and `DataManager` injected for future work.
- [x] Registered `ChartViewModel` and `DataViewModel` in `App.axaml.cs` -> `ConfigureServices`.
- [x] Verified every tab's `.axaml.cs` follows the `if (Application.Current is App app) DataContext = app.Services.GetRequiredService<TViewModel>();` pattern.

## Phase 10.5: Cache Performance [COMPLETED]
- [x] Profiled `CacheManager.LoadCacheAsync` -- delay was JSON deserialization, not disk I/O.
- [x] `System.Text.Json` source generation via `KlineJsonContext` -- no reflection on hot path.
- [x] Compact disk format: `KlineCompact` (flat `{T,O,H,L,C,V}` with `T` as Unix ms). ~40% smaller than the old `{Timestamp,Open,...}` format.
- [x] `FileStream` with 64 KB buffer + `FileOptions.SequentialScan | FileOptions.Asynchronous` for reads.
- [x] `DataManager` logs RAM hit / RAM miss so the memory tier is observable in `runtime-*.log`.
- [x] `CacheManager` logs per-save and per-load timing.
- [x] Legacy-format detection: `LoadCacheAsync` returns `null` on empty files, malformed JSON, or all-zero payloads (so `DataManager` refetches).
- [x] `BenchmarkAsync(barCount)` added for regression testing -- serialize/load N synthetic bars, log elapsed ms.
- [x] Verified: `XAUUSD` load 26 ms, `EURUSD` load 15 ms, subsequent runs are RAM hits. Strategy values nonzero after refetch.

## Phase 10.6: Data Tab Polish [COMPLETED]
- [x] Automatic size formatting: DataEntry.SizeDisplay computed property returns B / KB / MB / GB based on magnitude.
- [x] DataView.axaml Size column binds to SizeDisplay.
- [x] Size sort and filter continue to operate on the raw SizeBytes value.

## Phase 10.6.1: Data Tab Sort & Filter Indicators [COMPLETED]
- [x] Single sort glyph per column header, blank when the column is not the active sort.
- [x] `▲` / `▼` accent-colored when the column is the active sort.
- [x] Filter `TextBox` border + foreground turn accent (`#4FC3F7`) when the filter is non-empty.
- [x] `DataViewModel`: added five `*SortGlyph` and five `HasFilter*` computed properties.
- [x] Removed the "Clear Filters" toolbar button; filters self-clear per-column by deleting the text.

## Phase 10.7A: Charting Foundation [COMPLETED]
- [x] Charting library selected: OxyPlot.Avalonia 2.1.0-Avalonia11 (after ScottPlot.Avalonia white-background regression).
- [x] Removed `ScottPlot.Avalonia 5.0.55`; added `OxyPlot.Avalonia 2.1.0-Avalonia11`.
- [x] Theme include: `<StyleInclude Source="avares://OxyPlot.Avalonia/Themes/Default.axaml"/>`.
- [x] Dark chart background (`#1E1E1E`) renders on both `PlotModel.Background` and `PlotAreaBackground`.
- [x] `ChartViewModel`: `[ObservableProperty] List<Kline> Series`, `SelectedSymbol`, `SelectedTimeframe`, `PlotModel`.
- [x] Symbol picker: mirror of Strategies-tab pattern (ComboBox of cached symbols + free-text + refresh).
- [x] Data flow: `DataManager.GetSymbolDataAsync(symbol, timeframe)` -> rebuild `PlotModel` on `OnSeriesChanged`.
- [x] Candlestick rendering via `CandleStickSeries` (right-side price axis, no gridlines, year-only date labels).
- [x] Custom tracker popup -- `#2A2A2A` background, white text, `#888888` crosshair lines via `DefaultTrackerTemplate`.
- [x] Context menu with scale options: Auto, Logarithmic, Percent, Indexed to 100, Invert scale.
- [x] Percent and Indexed-to-100 transforms applied to OHLC items at construction time.

## Phase 10.7B: Volume [COMPLETED]
- [x] Volume subplot added below the price panel (price 0.22-1.00, volume 0.00-0.18 in `PlotModel` panel fractions).
- [x] Volume bars colored by candle direction: green (`#26A69A`) when `Close >= Open`, red (`#EF5350`) otherwise.
- [x] Shared X-axis between price and volume panels via a single bottom `DateTimeAxis`.
- [x] TradingView-style abbreviated volume labels on the right (`1.2K` / `1.2M` / `1.2B` / `1.2T`) via `LabelFormatter`.
- [x] Workaround: `RectangleBarSeries.UpdateData()` throws `NotImplementedException` in OxyPlot.Avalonia 2.1.0 when a series is reset from populated to empty. Replaced with two `LinearBarSeries` (one per color).

## Phase 10.7F: SkiaSharp Render Path [COMPLETED]
- [x] Migrated charting library from OxyPlot to ScottPlot 5.0.55 for hardware-accelerated SkiaSharp rendering.
- [x] Resolved pan/zoom rendering lag associated with Avalonia's generic drawing context.
- [x] Dynamically calculated bar widths to prevent timeframe overlap.
- [x] Implemented volume subplot using a secondary left axis to prevent volume and price autoscaling collisions.
- [x] Removed OxyPlot theme dictionary dependency from `App.axaml`.

## Phase 10.8A: Metric Taxonomy [COMPLETED]
- [x] `IMetric` extended with `MetricLag { Leading, Lagging, Mixed }` and `MetricCategory { Trend, Momentum, Volatility, Volume, Support, Breadth, Statistical }`.
- [x] `IMetric` now exposes `Name`, `Lag`, `Category`, `Description`, `Calculate(List<Kline>)`.
- [x] `BaseMetric` abstract class provides the shared shape for all implementations.
- [x] `MetricRegistry` exposes `Metrics` (`IReadOnlyCollection<IMetric>`) so the UI can render metadata.
- [x] `MetricsViewModel` exposes `MetricInfo` records (Name, Lag, Category, Description), sorted by category then name.
- [x] `MetricsView.axaml` renders the taxonomy with a compiled `DataTemplate x:DataType="vm:MetricInfo"` -- no `$parent` bindings.
- [x] `NullMetric` (internal singleton, `Calculate => 0`). `GetMetric` returns it on a miss so strategy scripts never NRE or throw. `TryGetMetric` added for callers that need a boolean.

## Phase 10.8B: Metric Suite Expansion [COMPLETED]
- [x] `CvdMetric` -- Cumulative Volume Delta approximated as `Volume * (2*(Close-Low)/(High-Low) - 1)` summed across the series (CLV x Volume). Guards `range <= 0`. Description states the method.
- [x] `RsiMetric` -- 14-period Wilder smoothing. Flat series (avgGain == 0 && avgLoss == 0) returns 50; upside-only returns 100. `Period` is a `const int`.
- [x] `AtrMetric` -- 14-period Wilder smoothing, `Period` as `const int`.
- [x] `SessionHighMetric` -- highest High across the series (restored; required by ARCHITECTURE §3 and Phase 1-4).
- [x] `ZScoreMetric` -- standard score of the last Close vs mean and population stddev (restored). Returns 0 when count < 2 or variance is zero.
- [x] `VolumeProfilePocMetric` / `VolumeProfileVahMetric` / `VolumeProfileValMetric` -- POC and 70% Value Area bounds.
- [x] `VolumeProfileEngine` -- two-pass routine (min/max/totalVolume sweep, then binning sweep). No LINQ in the hot path. `Math.Clamp` removed from the inner bin loop; bounds clamped once per candle. Value-area expansion terminates explicitly via `else { break; }`.
- [x] Metrics are pure and stateless -- no memoisation, no shared cache, no cross-call state. Correct every call. Interface unchanged (`double Calculate(List<Kline>)`) so existing strategy scripts are untouched.
- [x] `MetricRegistry` constructor registers all nine metrics. `HelloVwap.cs` still runs unchanged.
- [x] Build verified: `dotnet build --no-incremental` -- 0 errors.

## Phase 10.9: Runner & Presets [COMPLETED]
- [x] `src/core/Runner/` folder: Trade, TradeRecorder, StrategyParameter, StrategyParameterParser, PresetService, RunnerScriptGlobals.
- [x] `TradeRecorder` — Advance/Enter/Exit API. Timestamps from `Data[index]`.
- [x] `StrategyRunResult.Trades` — `IReadOnlyList<Trade>`.
- [x] `@param` declaration format + parser.
- [x] `RunnerScriptGlobals` gains `Trades`, `Params`.
- [x] `StrategyManager.RunBacktestFromFileAsync(path, data, parameters, ct)`.
- [x] `PresetService` — CRUD over `config/presets/{Strategy}_{Preset}.preset.json`.
- [x] `ConfigManager.ConfigDirectory` property.
- [x] `RunnerViewModel` + `RunnerView` — three-column layout (strategy list, source preview, scrollable params).
- [x] `PresetPickerWindow` — modal, DataGrid, Show-all toggle.
- [x] Strategies tab removed; Runner added as second tab.
- [x] `data/strategies/ParamTest.cs` test strategy.

## Phase 10.10A: Account Model & Service [COMPLETED]
- [x] `src/core/Accounts/AccountPhase.cs` — record for one challenge/funded phase. All rule fields nullable.
- [x] `src/core/Accounts/AccountConfig.cs` — record for a full account: Firm, Plan, Currency, StartingCapital, Phases array, ConsistencyScoreRule, Notes.
- [x] `src/core/Accounts/AccountJsonContext.cs` — source-generated System.Text.Json context. Camel-case, write-indented, nulls omitted.
- [x] `src/core/Accounts/AccountService.cs` — singleton. Scan data/accounts/ for *.account.json (filenames only), lazy JSON load, active selection, settings-backed persistence.
- [x] `AccountDescriptor` — FilePath, FileStem. Catalog row, no JSON read.
- [x] `AccountService.SetActive(descriptor)` writes `AppSettings.ActiveAccountName` and raises `ActiveChanged`.
- [x] `AppSettings.ActiveAccountName` — string property, auto-renders in Config tab via dynamic reflection engine.
- [x] Three seed accounts shipped: FTMO 1-Step 100K, FTMO 2-Step 100K, Maven Standard 2-Step 50K.
- [x] No FileSystemWatcher — manual Refresh only. Accounts change rarely.

## Phase 10.10B: Accounts Tab GUI [COMPLETED]
- [x] `src/gui/Accounts/AccountViewModel.cs` — Accounts list, PhaseRules (ObservableCollection of AccountRuleRow), SelectedAccount, ActiveAccountName banner, HasSelection.
- [x] Commands: `RefreshCommand`, `SetActiveCommand`, `OpenFolderCommand`.
- [x] `AccountView.axaml` — three-column grid (account list, splitter, detail pane).
- [x] Detail pane renders per-phase rules as label/value rows. Funded stage included as last phase.
- [x] `AccountView.axaml.cs` — DI wiring via `Application.Current is App app` pattern.
- [x] `AccountService` and `AccountViewModel` registered in `App.axaml.cs`.
- [x] Accounts tab inserted in MainWindow.axaml between Metrics and Data.
- [x] Verified: three seed accounts load, Set Active persists to settings.json, Open Folder opens data/accounts/, Config tab shows activeAccountName text box.

## Phase 10.11A: LastRunStore & OverviewData [COMPLETED]
- [x] `src/core/Overview/OverviewData.cs` — record: StrategyName, StrategyPath, Symbol, Timeframe, RunUtc, Duration, Trades, SourceData, Error.
- [x] `src/core/Overview/LastRunStore.cs` — singleton with `Current`, `Publish(data)`, `Changed` event. Lock-protected.
- [x] `RunnerViewModel` gains `LastRunStore` constructor dependency. Publishes into it after every run, including failed runs (Error populated).
- [x] `LastRunStore` registered as singleton in `App.axaml.cs`.
- [x] `src/gui/Overview/` — `OverviewViewModel`, `OverviewView.axaml`, `OverviewView.axaml.cs` (placeholder layout; full metrics in 10.11D).
- [x] `OverviewViewModel` registered as transient in `App.axaml.cs`.
- [x] Overview tab inserted as first tab in MainWindow.axaml, before Runner.
- [x] Verified: Runner run publishes into LastRunStore, Overview header populates with strategy name, symbol, trade count, duration, timestamp.

## Phase 10.10A (v2): Firm-Level Account Model [COMPLETED]
- [x] Replaced per-size JSON files with one JSON per firm.
- [x] `FirmConfig` / `FirmPlan` / `PlanSize` records replace `AccountConfig`.
- [x] `AccountJsonContext` now serializes `FirmConfig`.
- [x] `AccountService` expands a firm JSON into N `AccountDescriptor` entries (one per plan+size).
- [x] Firm JSON cache — parsed `FirmConfig` reused across descriptor expansion.
- [x] `AppSettings.ActiveAccountFirmFile` / `ActiveAccountPlan` / `ActiveAccountSize` replace `ActiveAccountName`.
- [x] `AccountService.ResolvePlanSize(descriptor)` returns the concrete `PlanSize`.
- [x] `data/accounts/FTMO.account.json` — 2 plans × 5 sizes.
- [x] `data/accounts/Maventrading.account.json` — 7 plans × 6 sizes.
- [x] `data/accounts/_TEMPLATE.account.json` — schema reference with placeholders.

## Phase 10.10B (v2): Accounts Tab [COMPLETED]
- [x] `AccountViewModel` displays `Firm` (bold) + `Plan · Size` (subtitle) per row.
- [x] Active banner shows `Firm · Plan · Size`.
- [x] Detail pane renders resolved `PlanSize.Phases` as label/value rows, plus `DrawdownType` and consistency rule.
- [x] Set Active persists the triple to `settings.json`.

## Phase 10.11A: LastRunStore & OverviewData [COMPLETED]
- [x] `src/core/Overview/OverviewData.cs` — record: StrategyName, StrategyPath, Symbol, Timeframe, RunUtc, Duration, Trades, SourceData, Error.
- [x] `src/core/Overview/LastRunStore.cs` — singleton with `Current`, `Publish(data)`, `Changed` event.
- [x] `RunnerViewModel` publishes into `LastRunStore` after every run.
- [x] `OverviewViewModel` + `OverviewView.axaml` placeholder, first tab in MainWindow.axaml.
- [x] Registered as singletons/transients in `App.axaml.cs`.
- [x] Verified: Runner run populates Overview header with strategy + trade count.

## Phase 10.11B: Monte Carlo Core [COMPLETED]
- [x] `src/core/Overview/MonteCarloResult.cs` — 6 raw distributions (NetProfits, MaxDrawdowns, AnnualReturns, Sharpes, ProfitFactors, MaxConsecutiveLosses), metadata (Iterations, MeanBlockSize, StartingCapital, RuinThresholdPercent, BacktestYears), RiskOfRuin, and Mean/Median/StdDev/P05/P95 aggregates.
- [x] `src/core/Overview/MonteCarloEngine.cs`:
  - Stationary block bootstrap (geometric block-length distribution).
  - `Random.Shared` for thread-safe parallel, per-iteration `new Random(seed + i)` when a seed is provided.
  - `Parallel.For` with explicit `MaxDegreeOfParallelism = Environment.ProcessorCount`.
  - Pre-allocated `double[]` result arrays. No `List<T>.Add`, no LINQ in hot loop.
  - Per-simulation: net equity, max DD %, max CL, gross profit / loss, sum of returns and sum of squares.
  - Sharpe computed from per-trade return statistics: `(mean/stdev) * sqrt(252)`.
  - Annual Return: `(finalEquity/startingCapital)^(1/years) - 1` in percent.
  - Profit Factor: `grossProfit / grossLoss`, capped at 999 when there are no losses.
  - Risk of Ruin: fraction of simulations where final equity < `startingCapital * (1 - ruinThresholdPercent/100)`.
  - `IProgress<int>` reports every `iterations / 20`.
- [x] `AccountPhase.DrawdownType` — `"static"` / `"trailing-eod"` / `"trailing-intraday"`.
- [x] `MonteCarloEngine` registered as singleton in `App.axaml.cs`.
- [x] Not yet wired to a ViewModel — that is Phase 10.11C.

## Phase 10.11C: Monte Carlo Tab GUI [COMPLETED]
- [x] `src/gui/MonteCarlo/MonteCarloViewModel.cs` — reads `LastRunStore.Current`, `AccountService.Active`, subscribes to both `Changed`/`ActiveChanged` events, clears stale results on new runs.
- [x] `McStatRow` record — Metric / Mean / Median / StdDev / P05 / P95.
- [x] Six metric groups: Net Profit, Max Drawdown %, Annual Return %, Sharpe, Profit Factor, Max Consecutive Losses.
- [x] `MonteCarloView.axaml` — three-column grid (stats, splitter, plot), toolbar with Iterations / Block Size / Ruin Threshold / Run button / ProgressBar / active account name.
- [x] `NumericUpDown` spinners hidden via `ShowButtonSpinner="False"`.
- [x] Ruin Threshold default reads from the active account's funded-phase `MaxLossPercent`.
- [x] Net Profit histogram via `ScottPlot.Statistics.Histogram.WithBinCount`, 40 bins, p05/median/p95 vertical lines.
- [x] `MonteCarloView.axaml.cs` — DI resolution, `SetPlotControl(PlotControl)`.
- [x] Monte Carlo tab inserted in `MainWindow.axaml` between Runner and Metrics.
- [x] `MonteCarloViewModel` registered as transient in `App.axaml.cs`.
- [x] Equity curve spaghetti plot deferred to 10.11C2 — `MonteCarloResult` stores final values only.
- [x] Verified: run ParamTest with 10 trades → MC tab renders histogram + summary stats.

## Phase 10.12A: LogManager Core [COMPLETED]
- [x] `LogManager` becomes static. The only static class in the codebase.
- [x] Initialized in `Program.Main` before `BuildAvaloniaApp()` — logging works even if Avalonia XAML or `MainWindow` construction fails.
- [x] `LogTag` enum: App, Cache, Data, Provider, Strategy, Runner, Account, MonteCarlo, Overview, Chart, Config, Metric.
- [x] Severity API: `Info`, `Warn`, `Debug`, `Error(tag, msg, ex)`. All static.
- [x] Serilog output format: `{Timestamp} [{Level:u3}] [{Tag}] {Message}`.
- [x] `flushToDiskInterval: 500ms`, one file per run at `logs/runtime-{yyyyMMdd-HHmmss}.log`.
- [x] Ring buffer (1000 entries) exposed via `GetRecentEntries()`. `Logged` event fires after every write with `LogEntry(Timestamp, Level, Tag, Message)`.
- [x] `CurrentLogFilePath` exposed for the Logs tab's Refresh-from-file.
- [x] No filtering. `MinimumLevel.Verbose()` on Serilog. `SetMinimumLevel` kept as no-op for API compatibility.
- [x] Top-level exception in `Program.Main` writes to log then rethrows.
- [x] `LogManager` removed from DI. All consumers call the static API directly.
- [x] Call sites tagged and migrated in: `CacheManager`, `DataManager`, `StrategyManager`, `AccountService`, all four providers, `ConfigViewModel`, `MainViewModel`, `LogsViewModel`.
- [x] Debug-level coverage added: RAM hit/miss, provider attempt index, firm file expansion counts, compile cache hits, provider discovery.
- [x] Verified: run a strategy → log shows full pipeline trace from Process start through Process exit (clean).

## Phase 10.13: VWAP Zero-Volume Fallback [COMPLETED]
- [x] `VwapMetric.Calculate` falls back to arithmetic mean of typical prices when total volume is 0.
- [x] `TwelveDataProvider` rejects results where all bars have volume=0, forcing fallback to the next provider.
- [x] Downgraded unsupported timeframe and empty-response log lines from Error to Warn.
- [x] Metric stays pure: no logging inside Calculate.
- [x] Confirmed root cause by inspecting cache: Twelve Data returns `"v":0` for XAU/USD on all timeframes.
- [x] Envelope format change: `CacheEnvelope` wrapper persists source, fetchedUtc, symbol, timeframe.

## Phase 10.14A: Queue Model + Data Tab [COMPLETED]
- [x] `SelectionQueue` singleton (`src/core/SelectionQueue.cs`) — `ObservableCollection<QueuedDataset>` with Add/Remove/Clear/Contains/Snapshot/Changed.
- [x] `AppSettings.QueuedDatasets` — list of `"symbol@timeframe"` strings in `config/settings.json`.
- [x] Persistence verified: `restored 3 queued dataset(s) from settings` on startup.
- [x] `DataRowViewModel` — wraps `DataEntry`, adds `IsQueued` observable write-through to `SelectionQueue`.
- [x] `DataView.axaml` — leftmost `On` checkbox column, `Queue…` toolbar button, `N queued` counter.
- [x] `QueueViewModel` + `QueueWindow` — modal with snapshot of queue. Unchecking marks `PendingRemoval`. `Done` commits, `Cancel` reverts, X button behaves like Done.
- [x] Pending removal visual: dimmed to 45% opacity.
- [x] `SelectionQueue` registered as singleton in `App.axaml.cs`.
- [x] Runner and Chart still use their own pickers; migrate in 10.14B/C.

## Phase 10.14B: Runner Batch Execution [COMPLETED]
- [x] `RunnerViewModel` reads the `SelectionQueue` instead of its own symbol/timeframe pickers.
- [x] `RunAsync` iterates every queued dataset, runs the strategy against each.
- [x] Confirmation modal shown when queue has more than one dataset.
- [x] `LastRunStore` holds `IReadOnlyList<OverviewData>`; `Changed` event passes the batch.
- [x] `RunnerView.axaml` toolbar replaced with `Run on: N dataset(s) queued.` display.
- [x] Verified: 3 queued datasets → 3 strategy executions, three log entries.

## Phase 10.14C: Chart Consumption [COMPLETED]
- [x] `ChartViewModel` reads queue; single `Dataset` dropdown bound to `QueuedItems`.
- [x] `ChartView.axaml` toolbar reduced to `Dataset` picker + `Reload` + `ProgressBar`.
- [x] Removed `AvailableSymbols`, `AvailableTimeframes`, `RefreshSymbolsCommand`.
- [x] Removed dead `src/gui/Strats` folder.
- [x] Renamed `OverviewModel.cs` → `OverviewViewModel.cs` (cosmetic; class was already correct).


## Phase 10.14E: Data Tab Fetch Form [COMPLETED]
- [x] `DataViewModel` gains `FetchSymbol`, `FetchTimeframe`, `IsFetching`, `FetchProgress`, `IsFetchProgressIndeterminate`, `FetchStatus`.
- [x] `AvailableTimeframes` populated from `TimeframeMap.All` in the constructor.
- [x] `FetchCommand` — calls `DataManager.GetSymbolDataAsync`, refreshes the row list on success, does not touch the queue.
- [x] `FetchAndQueueCommand` — same fetch, then writes to `SelectionQueue` with the provider name resolved from the cache metadata of the freshly written file.
- [x] Both commands guard on `IsFetching` and on empty symbol; progress marshalled via `Progress<DataProgress>`.
- [x] `DataView.axaml` gains a fetch row above the existing toolbar: symbol TextBox, timeframe ComboBox, Fetch and Fetch & Queue buttons, progress bar, status text.
- [x] Verified: fetch a cache-miss symbol/timeframe, row appears in the grid, Fetch & Queue increments the queue counter.

## Phase 10.15A: Trade extension [COMPLETED]
- [x] `Trade` record gains `EntryIndex`, `ExitIndex`, `Size`.
- [x] `TradeRecorder.Enter(side, price, size = 1.0)` — backward compatible.
- [x] `PnL` is size-scaled; `PnLPercent` is per-unit.

## Phase 10.15B: TimeSlice + builder [COMPLETED]
- [x] `TimeSlice` readonly struct — `Timestamp`, `MtmHigh`, `MtmLow`, `RealizedPnl`, `IsEodReset`.
- [x] `TimeSliceBuilder.Build(trades, klines)` — per-slice intra-trade MTM, exit-slice realized PnL, day-boundary EOD flag.
- [x] Allocation-free: one array, no LINQ, no lists.


## Phase 10.15C: Time-Domain MonteCarloEngine [COMPLETED]
- [x] `MonteCarloEngine.Run` rewritten. Signature: `Run(TimeSlice[] slices, int targetDurationSlices, double startingCapital, string drawdownType, double maxDailyLossPercent, double maxLossPercent, double profitTargetPercent, int expectedBlockSize, int? seed, IProgress<int>? progress)`.
- [x] Stationary block bootstrap (Politis & Romano). Block start uniform, block length geometric with mean `expectedBlockSize`, circular wrap across the slice array.
- [x] Flat-bet absolute PnL compounding: `balance += slice.RealizedPnl`. No percentage compounding.
- [x] Per-slice state machine: MTM-low check against daily floor and max-DD floor, then realize PnL, then update HWM per `DrawdownType`, then EOD reset.
- [x] `DrawdownType` handling:
  - `static` — max floor fixed at `startingCapital - maxFloorLoss`.
  - `trailing-eod` — max floor from HWM, HWM updated only at EOD (`slice.IsEod`).
  - `trailing-intraday` — max floor from HWM, HWM updated continuously on realized balance.
- [x] Daily floor anchored to `dayStartBalance`, reset on `slice.IsEod`.
- [x] Early exit on blown or funded. `SlicesElapsed` recorded at termination.
- [x] `MonteCarloResult` reshaped: `TerminalBalances[]`, `Outcomes[]` (byte: 0=blown, 1=funded, 2=timeout), `SlicesElapsed[]`, `MaxDrawdowns[]`; `PRuin`, `PFunded`, `PTimeout`, `ExpectedSlicesToFund`, `ExpectedSlicesToRuin`; terminal-balance Mean/Median/StdDev/P05/P95; max-drawdown Mean/Median/P05/P95; metadata `Iterations`, `ExpectedBlockSize`, `StartingCapital`, `TargetDurationSlices`, `DrawdownType`.
- [x] `MonteCarloOutcome` static class exposes `Blown = 0`, `Funded = 1`, `Timeout = 2`.
- [x] `Random.Shared` in parallel mode; per-iteration `new Random(seed + i)` when seeded.
- [x] Pre-allocated arrays per iteration. No `List<T>.Add`, no LINQ in the hot loop.
- [x] `MonteCarloViewModel` reworked:
  - Builds `TimeSlice[]` once per run via `TimeSliceBuilder.Build(data.Trades, data.SourceData)`.
  - Reads funded-phase rules (`MaxLossPercent`, `MaxDailyLossPercent`, `ProfitTargetPercent`, `DrawdownType`, `TradingPeriodDays`) from `AccountService.Active` → `ResolvePlanSize` → last phase.
  - Refuses to run if no active account or no resolved phases.
  - `ComputeTargetSlices` derives slices-per-day from the `SourceData` timestamp span, then multiplies by `TradingPeriodDays` (fallback 90).
  - Stats rows replaced: `P(ruin)/P(funded)/P(timeout)`, `Expected Slices`, `Terminal Balance`, `Max Drawdown %`.
  - Histogram is now terminal-balance distribution with p05/median/p95 vertical lines.
- [x] `MonteCarloView.axaml` unchanged for this phase — binds the same `McStatRow` shape and toolbar controls.


## Phase 10.15D: MonteCarloViewModel rework [COMPLETED]
- [x] Batch awareness: `RunAsync` iterates every entry in `LastRunStore.Batch` and runs the engine independently per dataset. Results stored in `_resultsByIndex` keyed by batch index.
- [x] Per-dataset outcome list: `McBatchRow` (`Display`, `PRuinText`, `PFundedText`, `PTimeoutText`) bound to `BatchResults`. Columns: P(ruin) red, P(funded) green, P(timeout) grey.
- [x] No aggregation. N datasets → N independent `MonteCarloResult` instances. No trade pooling, no mixed histogram.
- [x] Dataset selector: the existing `SelectedBatchItem` combo now drives which result is displayed in the stats panel and histogram. Selection does not gate the run.
- [x] `AutoScale` fix: `plt.Axes.AutoScale()` called after adding bars and percentile lines.
- [x] Roadmap bullets for `Annual Return` / `Sharpe` fixes struck as moot — those fields were removed from `MonteCarloResult` in 10.15C.
- [x] `MonteCarloView.axaml` restructured: stats panel now has a per-dataset outcomes block (visible only when `HasResults`) above the selected-dataset stats block.
- [x] Verified end-to-end: single dataset run shows 100% timeout on a 10-trade sample with a tight terminal-balance distribution; three-dataset batch run populates the per-dataset list and switching the combo swaps the histogram.


## Phase 10.15E: Overview batch summary [COMPLETED]
- [x] `OverviewViewModel` reads `LastRunStore.Batch` and rebuilds a per-dataset table on every `Changed` event.
- [x] `OverviewRow` record — `SymbolDisplay`, `TradesText`, `NetPnlText`, `WinRateText`, `DurationText`, `Status`. One row per batch entry.
- [x] `OverviewAggregate` record — `TotalTradesText`, `TotalNetPnlText`, `WeightedWinRateText`, `TotalDurationText`. Computed by summing per-dataset values, not pooling trades.
- [x] Win rate computed per dataset as `count(trade.PnL > 0) / tradeCount × 100`; aggregate win rate weighted by trade count.
- [x] Net PnL per dataset = `sum(trade.PnL)`; aggregate = sum of per-dataset NetPnL.
- [x] Header block retains the most recent run's strategy name, symbol, and single-run info. `BatchSummary` banner now reports `{ok}/{total} succeeded, {trades} trades across {N} dataset(s)`.
- [x] `OverviewView.axaml` renders a `DataGrid` of `OverviewRow` with columns Dataset / Trades / Net PnL / Win % / Duration / Status, plus a pinned aggregate footer row below the grid.
- [x] Verified end-to-end: 3 queued datasets → 3 rows + aggregate footer with summed trades, summed NetPnL, weighted win rate, summed duration.

## Phase 11: Additional Providers [COMPLETED]
- [x] `docs/PROVIDER_GUIDE.md` — six-touchpoint checklist for adding an `IDataProvider` (provider class, `TimeframeMap`, `ConfigManager`, `.env`, `App.axaml.cs`, `AppSettings.ProviderOrder`).
- [x] `BinanceProvider` — keyless crypto spot klines. `GET https://data-api.binance.vision/api/v3/klines`. Paginates in 200-day windows (server cap on `startTime`/`endTime` span). Symbol pass-through; no volume filter.
- [x] `FinnhubProvider` — stock and forex candles. US equities pass through, forex resolves to `OANDA:{BASE}_{QUOTE}`. Returns empty when `status != "ok"` — historical candles are gated behind a paid plan on the free tier.
- [x] `FcsApiProvider` — forex, crypto, commodities. `FCS_PUBLIC_API_KEY`. Response is a JSON object keyed by Unix seconds, each value `{o, h, l, c, v}`.
- [x] Stooq provider — attempted, then removed. Key issuance is blocked by a JavaScript proof-of-work gate on stooq.com; no path to a working key.
- [x] `TimeframeMap` gains `ToBinance`, `ToFinnhub`, `ToFcs`, `ToStooq`.
- [x] `ConfigManager` gains `FinnhubApiKey`, `FcsApiKey`, `StooqApiKey` read-only properties. `NormalizeValue` maps placeholder sentinels (`key`, `placeholder`, `todo`, `xxx`) to empty so unregistered providers short-circuit without an HTTP call.
- [x] `AppSettings.ProviderOrder` extended to `["Twelve Data", "Tiingo", "Yahoo", "Massive", "Binance", "Finnhub", "FCS API", "Stooq"]`.
- [x] `App.axaml.cs` registers four new keyed transients.
- [x] `.env` gains `FINNHUB_API_KEY`, `FCS_PUBLIC_API_KEY`

---

## Phase 11: Data Automation [COMPLETED]

### Phase 11A -- Window detection + input simulation
- TradingViewWindowDetector.FindWindow via EnumWindows + PID match.
- InputSimulatorService with SendInput P/Invoke: FocusAndMaximize, ClickAt, LeftClick, PressLeftArrow, PressRightArrow.

### Phase 11B -- PrintWindow capture + crop + save
- DesktopDuplicator.CaptureFrame(IntPtr hWnd) using PrintWindow(hWnd, hdc, PW_RENDERFULLCONTENT).
- Swap from DXGI Desktop Duplication to PrintWindow to fix black frames on Chromium-based TradingView.
- Cropped to the calibrated region and saved as data/shots/{symbol}_{timeframe}_{index:D4}.png.

### Phase 11C -- Region + click-point calibration with mixed-DPI handling
- AutomationCalibrationService persists to config/data_automation.json.
- CalibrationOverlayWindow (defined inside DataAutomationView.axaml.cs) shows a frozen PrintWindow snapshot with a dimmed mask and green selection border. Supports Region (drag) and Point (single click) modes.
- Mixed-DPI: overlay re-fitted on ScalingChanged + Opened, final RenderScaling used to convert drag coordinates to physical pixels. Verified working with TradingView on 100% monitor + backs2 on 125% monitor.

### Phase 11D -- Automation tab GUI
- DataAutomationViewModel with Symbol, Timeframe (from TimeframeMap.All), Steps, Delay, Region display, Click Point display, Calibrate Region, Calibrate Click, Clear, Start Automation.
- DataAutomationView.axaml with ShowButtonSpinner="False" on every NumericUpDown.
- Run order: focus + maximize -> single click at calibrated point -> 200x right-arrow -> capture + left-arrow loop.

### Deferred
- OCR extraction of the shots into JSON. See ROADMAP.md Phase 11E.


---

## Phase 13: MetaTrader 5 Tick Bridge [COMPLETED]

Ship date: 2026-09-30.

Bridges raw MqlTick data from a running MetaTrader 5 terminal into backs2. **ZeroMQ was originally planned and then dropped** — MT5's native socket API plus a BCL `TcpListener` on the C# side covers the case with zero extra dependencies. The EA is a TCP **client** that connects out to backs2, because MQL5's socket API does not implement `SocketBind`/`SocketListen`/`SocketAccept` (server-side sockets are not supported by MT5).

### Phase 13A -- MQL5 bridge script [COMPLETED]
- `docs/Backs2TickBridge.mq5` — Expert Advisor (not a script). Version 1.06.
- **No inputs.** Host, port, timeout, tick cap are all `#define` constants. Every fetch parameter is commanded from C#.
- `OnInit` calls `SocketCreate` + `SocketConnect` to `127.0.0.1:5555` and installs a 200 ms timer.
- `OnTimer` polls `SocketIsReadable`, accumulates bytes into a line buffer, splits on `\n`, dispatches each JSON request.
- On disconnect, retries every 3 seconds in `OnTimer`.
- Actions: `PING`, `GET_TICKS`, `GET_TERMINAL_INFO`. Unknown actions return `{"status":"ERROR","message":"Unknown action: X"}`.
- `GET_TICKS` request: `{ "action":"GET_TICKS", "symbol":"GOLD.pro", "from_msc":1700000000000, "count":250 }`.
- Response is wrapped with the request's `id` echoed back: `{ "id":N, "status":"OK", "count":250, "next_from_msc":<ms+1>, "ticks":[...] }`.
- Tick fields serialized: `time_msc`, `bid`, `ask`, `last`, `volume`, `volume_real`, `flags`.
- `CopyTicks` hard cap per call is 2000; EA clamps `count` to `COPYTICKS_CAP`.
- Default window when `from_msc=0`: `TimeCurrent() - 86400` (last 24h).

### Phase 13B -- Mt5TickServer + Mt5TickService [COMPLETED]
- `src/core/MT5/Mt5Tick.cs`:
  - `Mt5Tick` readonly record struct — `TimeMsc`, `Bid`, `Ask`, `Last`, `Volume`, `VolumeReal`, `Flags`. `Timestamp` computed property. `FlagsDisplay` decodes the MQL5 flag bitmask (Bid=2, Ask=4, Last=8, Vol=16, Buy=32, Sell=64).
  - `Mt5TerminalInfo` record — `Company`, `Account`, `Server`, `Currency`, `Leverage`, `ServerTimeUtc`.
  - `Mt5PingResult` record — `Success`, `LatencyMs`, `Error`.
  - `Mt5FetchProgress` record — `Fetched`, `Total`, `Phase`, `NewTicks` (per-batch list for streaming UI updates).
- `src/core/MT5/Mt5TickServer.cs` — singleton. Owns `TcpListener` on `127.0.0.1:{Mt5Port}`. One client at a time (the EA reconnects on drop). Requests are tagged with a monotonically increasing `id`; responses are matched back to `TaskCompletionSource` waiters via a `ConcurrentDictionary`. Write path is serialized with a `SemaphoreSlim(1,1)`.
- `src/core/MT5/Mt5TickService.cs` — singleton. Public API:
  - `Task<Mt5PingResult> PingAsync(ct)`.
  - `Task<List<Mt5Tick>> FetchTicksAsync(string symbol, long fromMsc, int targetTicks, int chunkSize, IProgress<Mt5FetchProgress>?, ct)` — paginates by chunk until `targetTicks` is reached or the EA returns fewer than requested (broker ran out of history).
  - `Task<Mt5TerminalInfo?> GetTerminalInfoAsync(ct)`.
  - Trim is applied to `symbol` at the service boundary — a stray leading space becomes an unknown symbol to `CopyTicks` and returns error 4401.
  - Never throws. Returns empty list / null on failure. Logs with `LogTag.Provider`.

### Phase 13C -- Config + DI wiring [COMPLETED]
- `AppSettings.Mt5Host` (string, default `"127.0.0.1"`).
- `AppSettings.Mt5Port` (int, default `5555`).
- `AppSettings.Mt5TimeoutMs` (int, default `15000`) — per-request timeout. The EA's JSON serialization can take ~5 s for a 2000-tick batch; a 3 s timeout caused responses to arrive after the waiter gave up.
- All three auto-render in the Config tab via the reflection engine.
- `App.axaml.cs` registers `Mt5TickServer` and `Mt5TickService` as singletons, and calls `mt5Server.Start()` in `OnFrameworkInitializationCompleted` inside a try/catch so a port-bind failure logs and continues.
- `Mt5TickViewModel` registered as transient.
- No `IDataProvider` registration — tick data does not fit the Kline-centric provider contract.
- No new `LogTag` — MT5 uses `LogTag.Provider`.

### Phase 13D -- MT5 Ticks tab [COMPLETED]
- `src/gui/MT5/Mt5TickView.axaml`, `Mt5TickView.axaml.cs`, `Mt5TickViewModel.cs`.
- Tab position in `MainWindow.axaml`: after Data Automation, before Providers.
- Controls:
  - **Row 0:** `Ping` button, connection info label (`Server listening on 127.0.0.1:5555`), status text.
  - **Row 1:** Symbol TextBox, Lookback hours `NumericUpDown` (0-8760), Target ticks `NumericUpDown` (1-500000), Chunk size `NumericUpDown` (50-2000).
  - **Row 2:** Fetch Ticks, Stop, Terminal Info, Save CSV buttons + ProgressBar + progress text.
  - **Row 3:** `DataGrid` of ticks — Time (UTC), Bid, Ask, Last, Volume, Volume Real, Flags.
  - **Row 4:** Terminal info read-only text block.
- `Ticks` is an `ObservableCollection<Mt5Tick>`. The progress callback appends each batch as it arrives, so the grid fills incrementally instead of all at once at the end.
- `LookbackHours <= 0` sends `from_msc=0` to the EA, triggering its 24h default window. Positive values compute an explicit `from_msc = now - hours*3600*1000`.
- `ChunkSize` default is 250 (not 2000) — smooth progress at ~20 updates per 5000-tick fetch. Larger chunks make the progress bar meaningless and the grid pops in one shot.

### Phase 13E -- Mt5TickWriter (CSV) [COMPLETED]
- `src/core/Writers/Mt5TickWriter.cs` — the **only** file in the `Writers/` folder today.
- The folder exists for writers that do not fit the standard `CacheEnvelope<KlineCompact[]>` shape used by every existing provider. `CacheManager` remains the writer for all Kline data.
- `WriteAsync(symbol, ticks, outputDir?)` — flat CSV at `data/ticks/{symbol}_{yyyyMMdd-HHmmss}.csv`. Header: `time_msc,bid,ask,last,volume,volume_real,flags`.
- `ReadAsync(path)` — inverse, used to reload for inspection.
- `data/ticks/` is git-ignored (user-fetched data, same class as `logs/`).

### Phase 13F -- Deployment guide [COMPLETED]
- `docs/MT5_TICK_GUIDE.md` — setup walkthrough, protocol spec, known constraints.

### Deployment requirements
- MT5 → **Tools → Options → Expert Advisors**:
  - `Allow algorithmic trading` — checked.
  - `Allow DLL imports` — checked (native socket API does not require it, but some MT5 builds gate `SocketCreate` behind it anyway).
  - `Allow WebRequest for listed URL` — checked, with **`127.0.0.1`** in the list. MQL5's socket API honors the WebRequest whitelist for outbound connections; without the entry, `SocketCreate`/`SocketConnect` fail.
  - `Disable algorithmic trading when the account has been changed` — unchecked (the OANDA demo toggles this off automatically on every login).
  - `Disable algorithmic trading when the profile has been changed` — unchecked.
- The EA must be attached to a **live chart** in the main MT5 window. It cannot run in the Strategy Tester — the tester sandbox blocks all network calls (`SocketCreate FAILED, error 4014`).
- Symbol naming is broker-specific. OANDA exposes gold as `GOLD.pro`, not `XAUUSD`. `CopyTicks("XAUUSD", ...)` returns `error 4401` (ERR_HISTORY_NOT_FOUND) on OANDA.

### Known constraints
- **2000-tick cap per `CopyTicks` call.** Automatic pagination via `next_from_msc`.
- **First-call broker sync.** The first `CopyTicks` for a cold symbol can block up to 45 s while MT5 downloads history from the trade server. Raise `Mt5TimeoutMs` in the Config tab to 60000 if a first fetch times out.
- **MQL5 string concatenation is O(n²).** A 2000-tick response serializes in ~5 s. Chunk size 250 keeps each batch under ~600 ms and enables streaming updates.
- **Forex/metals ticks have empty `last`, `volume`, `volume_real`.** Bid/ask movement is the signal. `Last`, `Volume`, `VolumeReal` being zero is expected.
- **No DOM.** `SYMBOL_TICKS_BOOKDEPTH` is 0 for OTC forex CFDs. Order book is not implemented and is not viable for retail forex.

### Architecture decisions
- **Raw TCP, not ZeroMQ.** One client, one server, localhost. `System.Net.Sockets` from the BCL covers it with zero NuGet dependencies. ZeroMQ's value (many publishers, complex routing, unreliable networks) does not apply here.
- **EA is a client, C# is a server.** MQL5 sockets do not support server-side operations. This inverted topology is the only working pattern.
- **Tick data is not an `IDataProvider`.** The shape (`bid`/`ask`/`last`/`flags`, no timeframe) does not fit `List<Kline>`. `Mt5TickService` is a separate singleton consumed only by the MT5 Ticks tab.
- **All fetch parameters come from C#.** The EA has no inputs. Everything from symbol to chunk size to lookback is commanded by the client.

## Phase 14: MT5 Fetch Optimization & UX [COMPLETED]

Ship date: 2026-10-01.

Raised MT5 tick fetch throughput from ~2,300 t/s to ~16,000 t/s — a ~7× improvement. The wall is now the MT5 terminal's internal `CopyTicks` (~50 µs/tick warm), not anything on the backs2 side.

### Phase 14A -- Progress UX & ETA stream [COMPLETED]
- `Mt5FetchProgress` extended: `Percent`, `TicksPerSecond`, `EtaSeconds`, `CurrentChunk`, `TotalChunks` (all defaulted, so existing positional call sites stay source-compatible).
- `Mt5TickService.FetchTicksAsync` maintains a 5-chunk rolling window `Queue<(ElapsedMs, Ticks)>` over the fetch. Throughput = Δticks / Δms; ETA = remaining / throughput.
- `Mt5TickView.axaml` shows bar + `Fetched/Total` + `N t/s` + `ETA Xm` side by side.

### Phase 14B -- UI thread decoupling & bulk render [COMPLETED]
- New `RangeObservableCollection<T>` at `src/gui/MT5/RangeObservableCollection.cs`. `AddRange` writes to the backing `Items` list, then raises one `NotifyCollectionChangedAction.Reset`. Replaces N per-item `CollectionChanged` events with one — this is what makes 100k+ row DataGrid streaming usable.
- `Mt5TickViewModel.Ticks` is now `RangeObservableCollection<Mt5Tick>`.
- Progress callback no longer iterates and calls `.Add()` per tick.

### Phase 14C -- MQL5 serialization overhaul [COMPLETED]
- `Backs2TickBridge.mq5` v1.08 → v1.09.
- Byte-buffer serialization replaces MQL5 string concatenation. v1.07/v1.08 used `StringReserve` + `StringAdd`, which MQL5 still reallocates on every append — measured O(N²): 20,000 ticks took 39.9 s to serialize.
- New `AppendStr(uchar &dest[], int &pos, string s)` writes ASCII chars into a pre-sized `uchar[]` byte buffer. `SocketSend` takes the buffer directly.
- Performance: `ea_ser` dropped from **39,915 ms → 13.8 ms** for a 20,000-tick batch (~2900×).

### Phase 14D -- CSV streaming [COMPLETED]
- New `Mt5TickStreamWriter` class in `src/core/Writers/Mt5TickWriter.cs`. `OpenAsync` → `AppendAsync` per batch → `CompleteAsync`. `StreamWriter` over a 64 KB async `FileStream`, `AutoFlush=false`.
- `Mt5TickViewModel.FetchTicks` opens a stream writer before the fetch, wires `onBatch` to a bounded `Channel<IReadOnlyList<Mt5Tick>>` (capacity 64), drains it in a background `Task`. The socket read loop no longer awaits disk I/O.
- Memory drops from O(TotalTicks) to O(ChannelCapacity × BatchSize).
- `Mt5TickService.FetchTicksAsync` gained an `onBatch` parameter (`Func<IReadOnlyList<Mt5Tick>, Task>?`) that is awaited inside the loop — deliberate backpressure so a slow disk never buffers unbounded.

### Phase 14E -- EA-side batching + PREWARM [COMPLETED]
- EA v1.10 accepts `count` up to `MAX_REQUEST_TICKS=50000`. Internally loops `CopyTicks` in 2000-tick slices, chaining via `next_from_msc` inside EA RAM, appending into one `collected MqlTick[]`.
- New `PREWARM` action: a small (500-tick) `CopyTicks` call before the real fetch. First `CopyTicks` per symbol costs ~2× warm (tick-DB page-in on the MT5 side). Prewarm makes iter #1 run at warm speed.
- `Mt5TickService.PrewarmAsync` — idempotent per symbol per process via `HashSet<string> _warmedSymbols`. Never throws; drops from the set on failure so a later fetch retries.
- Default batch size raised from 250 (v1.07) to **20000** (v1.10). The 250 value existed only to keep the O(N²) serializer from blowing past `Mt5TimeoutMs`.
- `perf_copy_us` and `perf_ser_us` returned by the EA in every `GET_TICKS` response. C# log line splits `rtt` into `ea_copy` (with µs/tick) and `ea_ser`.

### Measured results
- Serialization: 39,915 ms → 13.8 ms for 20,000 ticks (~2900×).
- `ea_copy` warm: ~49–60 µs/tick (terminal's `CopyTicks` floor).
- `ea_copy` first-call-per-symbol: ~87 µs/tick (cold) → ~50 µs/tick after prewarm.
- End-to-end: 50,000 ticks in ~3.1 s wall = ~16,000 t/s. Ceiling is ~20,000 t/s (`CopyTicks`-bound).

### Known remaining constraints
- **`CopyTicks` is the wall.** ~50 µs/tick warm, terminal-internal, not controllable from the C# side.
- Prewarm cost is one extra round trip (~100–120 ms) on the first fetch per symbol per process. Subsequent fetches pay nothing.
- First-call cold-symbol 45 s broker sync remains (Phase 13, unchanged).

## Phase 14: MT5 Fetch Optimization & UX [COMPLETED]

Ship date: 2026-10-01.

Raised MT5 tick fetch throughput from ~2,300 t/s to ~16,000 t/s sustained, and eliminated the inclusive-boundary correctness bugs that had been silently dropping and duplicating ticks at every batch seam. Final verification: `VerifyTickRangeExact` returns a count byte-identical to the fetch, over 48 boundaries and ~960,000 ticks.

### Phase 14A -- Progress UX & ETA stream [COMPLETED]
- `Mt5FetchProgress` extended: `Percent`, `TicksPerSecond`, `EtaSeconds`, `CurrentChunk`, `TotalChunks` (all defaulted, so existing positional call sites stay source-compatible).
- `Mt5TickService.FetchTicksAsync` maintains a 5-chunk rolling window `Queue<(ElapsedMs, Ticks)>` over the fetch. Throughput = Δticks / Δms; ETA = remaining / throughput.
- `Mt5TickView.axaml` shows bar + `Fetched/Total` + `N t/s` + `ETA Xm` side by side.

### Phase 14B -- UI thread decoupling & bulk render [COMPLETED]
- New `RangeObservableCollection<T>` at `src/gui/MT5/RangeObservableCollection.cs`. `AddRange` writes to the backing `Items` list, then raises one `NotifyCollectionChangedAction.Reset`. Replaces N per-item `CollectionChanged` events with one.
- `Mt5TickViewModel.Ticks` is now `RangeObservableCollection<Mt5Tick>`.

### Phase 14C -- MQL5 serialization overhaul [COMPLETED]
- `Backs2TickBridge.mq5` v1.08 → v1.09.
- Byte-buffer serialization replaces MQL5 string concatenation. `AppendStr(uchar &dest[], int &pos, string s)` writes ASCII chars into a pre-sized `uchar[]` byte buffer. `SocketSend` takes the buffer directly.
- Performance: `ea_ser` dropped from **39,915 ms → 13.8 ms** for a 20,000-tick batch (~2900×).

### Phase 14D -- CSV streaming [COMPLETED]
- New `Mt5TickStreamWriter` class in `src/core/Writers/Mt5TickWriter.cs`. `OpenAsync` → `AppendAsync` per batch → `CompleteAsync`. `StreamWriter` over a 64 KB async `FileStream`, `AutoFlush=false`.
- `Mt5TickViewModel.FetchTicks` wires `onBatch` to a bounded `Channel<IReadOnlyList<Mt5Tick>>` (capacity 64), drains it in a background `Task`. The socket read loop never awaits disk I/O.
- `Mt5TickService.FetchTicksAsync` gained an `onBatch` parameter (`Func<IReadOnlyList<Mt5Tick>, Task>?`) that is awaited inside the loop — deliberate backpressure.

### Phase 14E -- EA-side batching + PREWARM [COMPLETED]
- EA v1.10 accepts `count` up to `MAX_REQUEST_TICKS=50000`. Internally loops `CopyTicks` in 2000-tick slices, chaining via `next_from_msc` inside EA RAM.
- New `PREWARM` action: a small (500-tick) `CopyTicks` call before the real fetch. First `CopyTicks` per symbol costs ~2× warm (tick-DB page-in). Prewarm makes iter #1 run at warm speed.
- `Mt5TickService.PrewarmAsync` — idempotent per symbol per process via `HashSet<string> _warmedSymbols`.
- Default batch size raised from 250 (v1.07) to **20000** (v1.10).
- `perf_copy_us` and `perf_ser_us` returned by the EA in every `GET_TICKS` response.

### Phase 14F -- Inclusive-cursor boundary correctness [COMPLETED]
- **Bug 1 (v1.09/1.10):** The EA sent `next_from_msc = lastMsc + 1`. `CopyTicks` treats `from_msc` as inclusive, so `+1` skipped every tick that shared a millisecond with the previous batch's tail. Measured: 344 missing ticks in a 959,628-tick fetch.
- **Bug 2 (v1.11):** The naive fix — remove `+1` and drop one leading duplicate on the C# side — was too aggressive. Multiple ticks often share a millisecond at batch boundaries (~2 per boundary on OANDA `GOLD.pro`), so dropping exactly one left K-1 duplicates per seam. Measured: +914 extra ticks over ground truth.
- **Final fix (v1.12, current):**
  - EA sends `next_from_msc = lastMsc` (inclusive). Its internal `CopyTicks` loop skips exactly `lastTimeTrailing` leading ticks on each subsequent slice, where `lastTimeTrailing` is the count of consecutive same-`time_msc` ticks at the tail of the previous slice. This makes the internal EA-side batching correct too.
  - C# `FetchTicksAsync` tracks `_lastEmittedTimeMsc` and `_lastEmittedTrailing` (post-dedupe), and drops exactly that many leading ticks from each batch after the first.
  - The broker-ran-out-of-history exit check now uses the **raw** count from the EA (before C# dedupe), not the deduped count. Comparing the deduped count against the requested size was falsely tripping on every batch after the first.
  - Log line now shows `kept N of M | ... dedupe=K (cumulative X, total-deduped Y)` so duplicates are visible in-line.
- **Verified:** `VerifyTickRangeExact` returns **960,958** for a fetch that returned **960,958**. Byte-exact. `96 boundary dupes dropped` across 48 boundaries (2 per seam).

### Measured results
- Serialization: 39,915 ms → 13.8 ms for 20,000 ticks (~2900×).
- `ea_copy` warm: ~35–60 µs/tick (terminal's `CopyTicks` floor, varies with DB temperature and concurrent load).
- `ea_copy` hot (4096-tick fast cache): ~0.03–0.05 µs/tick — up to 1500× faster on the tail.
- End-to-end: 50,000 ticks in ~3.1 s wall = ~16,000 t/s sustained. Ceiling is ~20,000 t/s (cold `CopyTicks`-bound).

### Known remaining constraints
- **`CopyTicks` is the wall.** ~35–60 µs/tick warm, terminal-internal, not controllable from the C# side.
- **MT5 keeps a 4,096-tick fast access cache per symbol.** Requests landing inside it run at ~0.03 µs/tick. The cache fills only from live tick flow, not from historical reads — backfilling a fresh symbol never fills it. Live-tail fetches (≤4096 ticks from `now`) hit it every time.
- Prewarm cost is one extra round trip (~100–120 ms warm, up to ~1 s cold) on the first fetch per symbol per process.
- First-call cold-symbol 45 s broker sync remains (Phase 13, unchanged).

## Phase 15: MT5 Tick Extraction, Verification & Parsing [COMPLETED]

Ship date: 2026-10-02.

Turns the MT5 Ticks tab from a raw fetch surface into a full tick-to-cache pipeline: fetch raw ticks, verify the fetch against the terminal's own tick database, then parse the tick CSV into standard kline cache that every other tab consumes like any provider dataset.

### Phase 15A -- Verify Range panel [COMPLETED]
- EA v1.13 adds a `COUNT_TICKS` action: calls `CopyTicksRange` directly and returns `count`, `first_msc`, `last_msc`, `perf_us`. Bypasses the fetch loop entirely; reads from the same tick DB the fetch reads from. `to_msc` is exclusive.
- `Mt5TickService.CountTicksAsync(symbol, fromMsc, toMsc, ct)` — sends the request, parses `Mt5CountResult`, logs the wall time and the adaptive timeout used.
- **Adaptive timeout**: `Mt5TimeoutMs + min(spanDays, 90) × 3000 ms`. The default 15 s is sized for 20k-tick fetch batches and falsely times out on multi-day range queries; a 30-day query takes ~44 s and would have been dropped. The +3 s/day scaling covers the range up to a 90-day cap.
- `Mt5CountResult` record — `Count`, `FirstMsc`, `LastMsc`, `PerfUs`.
- `Mt5TickView` gains a collapsible Verify Range `Expander` with:
  - `From` / `To` text inputs in `YYYY/MM/DD HH:MM:SS[.mmm]` (accepts `-`, `.`, `/` as date separators).
  - `UTC offset` NumericUpDown (±0.5 h steps) so the entered strings can be declared in any timezone.
  - Live resolved preview: From/To in UTC and a Span formatted as `Nd Nh Nm Ns`.
  - `Load Last Fetch Range` — fills From/To from the last fetch's `[fromMsc, endMsc + 1)` window.
  - `Verify` — calls `CountTicksAsync`, prints count + first/last tick deltas, compares against the last fetch's tick count with ✓ MATCH / ✗ MISMATCH in green/red.
  - `Apply to Fetch` — copies the range into the fetch parameters. Symbol and Batch size preserved; `LookbackHours` computed as ceil hours back from now; `TargetTicks` sized from the verified count with 1% headroom (min +1000, cap 50M).

### Phase 15B -- CSV → cache parsing [COMPLETED]
- `Mt5TickView` becomes a `TabControl` with two subtabs: **Extraction** (existing fetch + verify) and **Parsing**.
- Parsing subtab: CSV file picker rooted at `data/ticks/` (via `StorageProvider.OpenFilePickerAsync`), timeframe ComboBox (`TimeframeMap.All`), `Parse to Cache` button, progress bar, status line.
- `Mt5TickViewModel.ParseCsv` runs the whole parse inside `Task.Run` — CPU-bound, background thread, no UI thread re-entry. Progress marshalled via `IProgress<T>`.
- `ParseCsvCore`: 64 KB buffered `FileStream` with `FileOptions.SequentialScan`; span-based tick-line parser (`TryParseTickLine`) avoids per-token substring allocations; `CultureInfo.InvariantCulture` on both parses.
- **Bucketing** (`GetBucketStart`): epoch-relative for every intraday timeframe — `(timeMsc / tfMs) * tfMs`. Fixes the 45m case, which the previous per-hour anchoring broke (alternating 45- and 15-minute bars at hour boundaries). `1D` = UTC midnight; `1W` = ISO Monday UTC. Daily/weekly bars are UTC-aligned, which differs from MT5's server-day boundary (typically GMT+2/3 on OANDA) — documented limitation.
- **Zero-bid tick handling**: ticks with `bid <= 0` are dropped (forex/metals ticks carry `bid=0` on the broker's daily opening tick) and logged. Up to 10,000 are retained for the log message; >25 collapses to a count-only Warn.
- Saves via `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`. The dataset appears in the Data tab with `source = "MT5 Parsed"` and is immediately usable by the Chart, Runner, and Monte Carlo tabs.
- OHLCV built from `Bid`; `Volume` = tick count in the bucket. Symbol taken from the CSV filename before the first `_`.

### Phase 15C -- Grid cap and Populate Grid toggle [COMPLETED]
- `Mt5TickViewModel.MaxGridRows = 100_000`. Once the DataGrid hits the cap, `PopulateGrid` auto-unchecks and the grid stops growing. CSV streaming continues through the channel, so no data is lost — the CSV has every tick.
- `PopulateGrid` checkbox in the Extraction toolbar. Uncheck before a large fetch to skip the DataGrid bulk-inserts entirely; the fetch proceeds identically.
- `OnTargetTicksChanged` auto-unchecks `PopulateGrid` when `TargetTicks > 500_000` and shows a banner explaining why. Guards against the user typing a huge number and forgetting the checkbox.
- `GridCapText` banner above the DataGrid announces the cap or the auto-disable reason. Hidden when empty.
- Root cause of the pre-cap freeze: `RangeObservableCollection.AddRange` raises one `NotifyCollectionChangedAction.Reset` per batch, and Avalonia's `DataGrid` re-evaluates the whole collection on every Reset. At 20M+ rows that saturates the UI thread, which backs up `Progress`/`Logged` dispatches and inflates fetch RTT (measured: 1.3 s → 3.1 s over a 20M-tick fetch). Capping at 100k keeps the UI thread idle throughout.

## Phase 16: MT5 .tkc Cache Extraction [COMPLETED]

Ship date: 2026-10-02.

Reads MetaTrader's local monthly `.tkc` tick-cache files directly from disk and aggregates them into standard kline cache. Bypasses the EA bridge entirely — the terminal has already downloaded and compressed this data, so backs2 decodes it in-process. Full-history GOLD.pro (25 files, 153.6 M ticks) decodes in ~19 s.

### Phase 16A -- .tkc decoder [COMPLETED]
- `src/core/MT5/tck/` module:
  - `TkcModels.cs` — `TkcTick` (readonly record struct: `TimestampMs`, `Bid`, `Ask`), `TkcInfo` (`Magic`, `Symbol`, `FileSize`, `TickCount`, `StartMs`, `EndMs`), `TkcException`.
  - `TkcDecoder.cs` — static `Decode(path)` returning `(TkcInfo, IReadOnlyList<TkcTick>)`.
  - `TkcFile.cs` — `TkcFile` lazy wrapper (decode-once, cached), `TkcRepository.ListSymbolFiles(root, broker, symbol)`.
- Format reverse-engineered:
  - Magic `0x000001FD` at offset 0.
  - Symbol as UTF-16LE in `[0xA0, 0xD0)`.
  - Index starts at `0x00E4`, 48 bytes per entry, terminated by `size == 0` or non-monotonic `offset`.
  - Per-day payload is a sequence of interleaved zlib streams (`0x78` + one of `0x01 | 0x5E | 0x9C | 0xDA`).
- Records are 16 bytes:
  - Timestamps delta-encoded — first record is absolute Unix-ms, subsequent records are `u64` deltas accumulated into a running cursor.
  - Bid and ask are f64 stored in the **first or second half** of each record, detected per block by `DetectPriceOffset`. Never hard-code the offset.
- `ClassifyBlock` checks the timestamp anchor **first**: if the first 8 bytes decode to a Unix-ms value in `[1.42e12, 2.0e12]`, the block is `Timestamps`. Zero-fraction checking runs only afterwards. Delta-encoded timestamps can be 0 for many consecutive ticks (many ticks per millisecond), and the previous zero-fraction-first classifier tagged valid timestamp blocks as `Empty` and silently dropped entire days.
- `DecodeFromBlocks` walks blocks forward and decodes each `[Timestamps, Prices, Prices, ...]` group **independently**, concatenating the resulting tick chunks. The previous global-pick approach (single largest timestamp block, two closest-mid price blocks) silently dropped 95% of `202610.tkc`'s ticks on the 105-block day.
- `InflateZlib` takes the **full remaining** `[offset..offset+size)` — `ZLibStream` self-terminates at the zlib footer. The previous 2 MB slice cap silently truncated larger streams, producing partial or undecodable payloads.
- **Logging contract:** exactly **one** `Info` line per file (`TKC 'file': N ticks, D day(s), T ms`) plus at most one `Warn` at end (zero-bid count). Never add per-day, per-block, or per-stream logging to the decoder. A previous diagnostic pass at 60 lines/file buried a silent-tick-loss bug for hours.

### Phase 16B -- Extract from .tkc subtab [COMPLETED]
- `Mt5TickView.axaml` gains a third subtab: **Extract from .tkc**.
- Inputs: `TerminalDataRoot` (`{...}\MetaQuotes\Terminal\{GUID}`), `TerminalBroker` (e.g. `OANDATMS-MT5`), `TkcSymbol` (e.g. `GOLD.pro`), `TkcTimeframe` (`TimeframeMap.All`), `TkcLookbackDays` (0 = all), `TkcMaxTicks` (0 = all).
- Single action: **Extract & Save to Cache**. No scan step, no file grid.
- `TerminalDataRoot` and `TerminalBroker` are persisted to `AppSettings.Mt5TerminalDataRoot` / `Mt5TerminalBroker` on every run.

### Phase 16C -- Range limits [COMPLETED]
- `FileOverlapsLookback` cheap-parses `YYYYMM.tkc` filenames and skips whole months before the cutoff without decoding. Non-standard filenames are conservatively included.
- Files are iterated **newest-first**; ticks within a file are iterated in **reverse**. Both `TkcLookbackDays` and `TkcMaxTicks` therefore stop the decode mid-file without materialising every tick.
- Bucket semantics under reverse iteration: the **first** bid seen in a bucket is **Close**, the **last** is **Open**. High/Low are symmetric.
- Full-history GOLD.pro (25 files, 153.6 M ticks) → ~19 s. 30-day lookback → ~5 s. 5 M-tick cap → ~2 s.

### Phase 16D -- Cache write [COMPLETED]
- Bars are built from `Bid`, `Volume` = tick count. Zero-bid ticks (`Bid <= 0`) are dropped and counted.
- Timeframe bucketing is epoch-relative: `(timeMsc / tfMs) * tfMs`. Same rule as the CSV parser. `1D` = UTC midnight, `1W` = ISO Monday UTC.
- Written through `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`.
- The dataset appears in the Data tab like any other cached file and is immediately usable by Chart, Runner, and Monte Carlo.
- Verified: GOLD.pro@1h → 145,509,458 valid ticks (8,141,198 zero-bid dropped) → 10,365 bars.

### Design decisions
- **Serial across files.** Never parallel. The full-history GOLD.pro set holds ~3.6 GB of ticks in RAM if decoded in parallel. Peak memory stays bounded by the largest single file's tick list (~320 MB for `202603.tkc`) plus the bucket dict.
- **Double-decode is the right tradeoff.** `ScanTkc` (in the earlier iteration) decoded each file once for Info; `ExtractTkcCore` decodes again for ticks. There is no random-access index in the `.tkc` format, so tick count requires a full decode. Caching decoded ticks across both operations would retain GBs; caching only `Info` would still require decoding. Not worth optimising until decode time becomes a bottleneck.
- **Never run raw ticks through a strategy or metric.** `RunnerScriptGlobals.Data` is `List<Kline>`; `IMetric.Calculate` takes `List<Kline>`; `TimeSliceBuilder` produces one slice per Kline; `CacheManager` stores `KlineCompact[]`. Feeding ticks would require a parallel execution engine (new globals type, tick-aware metrics, tick-aware TradeRecorder, tick-aware Monte Carlo) and would silently break bar-based indicators (RSI-14 over 14 ticks, ATR = 0, etc.). Always aggregate to bars first.
- **Different timeframe = re-run with a different `TkcTimeframe`.** No intermediate tick cache tier. The decode is fast enough (~19 s full history) that a binary tick-cache tier would add complexity without a matching benefit.