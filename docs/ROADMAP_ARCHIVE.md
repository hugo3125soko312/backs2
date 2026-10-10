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

---

## Phase 12: Hardening & Housekeeping

### Phase 12A -- Data tab Modified column timezone [COMPLETED]

Ship date: 2026-10-03.

The Data tab's `Modified` column rendered the UTC instant of each cache file's mtime, not the local wall-clock time that Windows Explorer shows. On CEST (UTC+2) every row was exactly 2 hours behind disk. Not corruption — a `DateTime.Kind`-blind rendering bug.

- Root cause: `DataManager.GetAvailableData` populated `DataEntry.LastModifiedUtc` from `FileInfo.LastWriteTimeUtc`. That value flowed unmodified into `DataRowViewModel.LastModifiedUtc`, and the XAML binding `{Binding LastModifiedUtc, StringFormat='{}{0:yyyy-MM-dd HH:mm}'}` prints the raw clock components of a `DateTime` without honouring its `Kind`. The grid rendered the UTC clock; Explorer rendered the local clock. Same file, two different numbers.
- Fix: `DataEntry.LastModifiedUtc` → `DataEntry.LastModifiedLocal`, sourced from `FileInfo.LastWriteTime` (Kind = Local). Rename propagated through `DataRowViewModel`, the DataGrid column binding, the `ModifiedSortGlyph` key, the `ApplyView` filter predicate, and the `SortColumn` switch.
- The rename is deliberate: it forces every consumer to be recompiled against the new name, so no stale binding silently keeps rendering UTC. Sort and filter semantics unchanged — `DateTime` comparison is Kind-agnostic.
- Verified against `Get-ChildItem data\cache\*.json | Sort-Object LastWriteTime -Descending` — every row now matches to the minute.

$entry = @'

## Phase 17: Startup Path Optimization [COMPLETED]

Ship date: 2026-10-04.

Cold start measured at 3324 ms (`[App] Process start` → `App.OnFrameworkInitializationCompleted` total). After this phase: ~530 ms, a -84% improvement. The dominant cost was eager tab construction: `MainWindow.axaml` instantiated all 12 tab Views as direct children of `TabItem`, and each View code-behind resolved its own ViewModel via `Application.Current is App app -> app.Services.GetRequiredService<TViewModel>()`. Every tab's ViewModel -- and its transitive singleton dependencies -- was therefore constructed before the window could paint. `AccountService` alone accounted for ~450 ms (directory scan of `data/accounts/` plus three firm JSON parses).

### Instrumentation
- `App.axaml.cs` gains `Stopwatch` markers around the startup stages and `[startup]` Info log lines: DI container built, ConfigManager resolved, MT5 server started, MainViewModel resolved, MainWindow constructed, OnFrameworkInitializationCompleted total. Kept permanently as cold-start telemetry.
- Cold start is defined as `[App] Process start` -> `OnFrameworkInitializationCompleted total`. The acceptance metric is `[startup] MainWindow constructed in N ms`.

### Refactor: lazy tab host
- `MainViewModel` gains an `IServiceProvider` constructor parameter plus 12 lazily-resolved tab ViewModel properties (`OverviewVm`, `RunnerVm`, `MonteCarloVm`, `MetricsVm`, `AccountVm`, `DataVm`, `DataAutomationVm`, `Mt5TickVm`, `ProvidersVm`, `ChartVm`, `LogsVm`, `ConfigVm`), each `??= _sp.GetRequiredService<XxxViewModel>()`.
- `MainViewModel.CurrentVm` is a switch on `ActiveTabIndex` returning the correct property; `OnActiveTabIndexChanged` raises `PropertyChanged(nameof(CurrentVm))`.
- `MainWindow.axaml` becomes a headers-only `TabControl` (12 empty `<TabItem Header="..." />` elements) plus one sibling `ContentControl` bound to `CurrentVm`. Tab content is no longer declared inside `TabItem`.
- `App.axaml` gains `<Application.DataTemplates>` with one `<DataTemplate DataType="vm:XxxViewModel"><views:XxxView /></DataTemplate>` per tab. Avalonia resolves the DataTemplate when `CurrentVm` is set.

### View code-behind cleanup
- Seven Views become trivial (`InitializeComponent()` only): `OverviewView`, `RunnerView`, `MetricsView`, `AccountView`, `Mt5TickView`, `ProvidersView`, `ConfigView`.
- Three Views keep constructor-time wiring that does not depend on a specific ViewModel instance: `LogsView` (Enter-key tunnel handler on `CommandInputBox`), `MonteCarloView` (`UserInputProcessor.Disable()` on the three plots, `ClusterPlot.PointerPressed`, tunnel-phase `KeyDown`), `ChartView` (`UserInputProcessor.Disable()`, `PointerMoved`, `PointerExited`, tunnel-phase `KeyDown`).
- Four Views move DataContext-dependent wiring to `OnDataContextChanged`: `ChartView` (`SetPlotControl`), `MonteCarloView` (three plot setters), `DataView` (`OpenQueueRequested` subscription), `DataAutomationView` (three calibration events).

### Measured result
| Stage | Before | After |
|---|---|---|
| `Process start` -> OnFrameworkInit complete | 3324 ms | 530 ms |
| `[startup] MainWindow constructed` | 2937 ms | 157 ms |
| `[startup] OnFrameworkInitializationCompleted total` | 3019 ms | 235 ms |

Deferred to first-click (no longer on the startup path): Accounts tab (~450 ms -- directory scan + firm JSON parses), Data tab (queued-dataset restore + initial fetch, ~80 ms for the restored `GOLD.pro@1h`), Config tab (provider reflection discovery, ~15 ms).

### Deliberately out of scope
- **csproj compilation flags.** Proposed `<PublishReadyToRun>` and `<TieredCompilationQuickStartupJit>` are both no-ops for the `dotnet run` dev loop. `<TieredCompilationQuickStartupJit>` is not a real MSBuild property -- the runtimeconfig knob is `<TieredCompilationQuickJit>` and it already defaults to `true` on .NET 8. `<PublishReadyToRun>` only affects `dotnet publish` output.
- **Deferring I/O in `StrategyCatalogService` / `AccountService` constructors.** Moot after the tab-host refactor -- both singletons now resolve only when their consuming tab is opened. The first-click hit (~450 ms for Accounts) is acceptable. Left as a future item if first-click latency becomes a UX problem.
- **Deferring `Mt5TickServer.Start()`.** Forbidden by the `App.axaml.cs` comment requiring the listener to start before any view is constructed so the EA can connect immediately. Unchanged.

### Files touched
- `App.axaml.cs` (instrumentation only; DI registrations unchanged)
- `MainWindow.axaml`
- `App.axaml`
- `src/gui/MainViewModel.cs`
- 12 View code-behinds under `src/gui/`

### Rule added
`docs/RULES.md` -- Views must not resolve their own ViewModels from the DI container in the constructor. See the "Tab host & ViewModel resolution" rule.
'@
Add-Content -Path 'docs\ROADMAP_ARCHIVE.md' -Value $entry
## Phase 19: GUI Shell & Design Tokens [COMPLETED]

Ship date: 2026-10-07.

First pass of the professional GUI revamp. Establishes a design-token foundation and replaces the OS-chrome-plus-top-tabs shell with a custom title bar, VS Code-style menu, left nav rail, and two-cell status bar. The rest of the app's visual language (component patterns, per-tab reskins) is deferred to later phases.

### Design tokens
- `src/gui/Theme/Tokens.axaml` — ResourceDictionary of the full palette (surface ramp, text ramp, borders, accent, semantic), typography scale, spacing scale, radius, and shell dimensions. Merged into `App.axaml`.
- Palette is Grafana-adjacent: `#0D1117` app bg, `#161B22` panel bg, `#21262D` elevated, `#000000` sunken (plots). Text `#E6EDF3` / `#848D97` / `#484F58`. Accent `#2F81F7`, hover `#458EF7`, pressed `#176BE0`. Semantic `#238636` / `#D29922` / `#F85149`. Border `#30363D`.
- Typography: Inter for UI, Cascadia Code / Consolas for numerics. Sizes: 11 status, 12 grid, 14 body, 16 header.
- Spacing 4/8/12/16. Radius 2px. Row height 26px. Title bar 32px. Status bar 24px. Nav rail 180px.

### Shell
- Custom title bar via `ExtendClientAreaToDecorationsHint` + `NoChrome`. Menu bar with File, Data, Strategy, Analysis, Account, View, Window, Help — most items are placeholders pending later phases.
- Left nav rail (`ListBox` bound to `ActiveTabIndex`) replaces the horizontal `TabControl`. 3px accent stripe on the selected item.
- Two-cell status bar at the bottom: `StatusText` on the left, static placeholder on the right.
- `MainViewModel.Shell.cs` holds 12 navigation commands — pure `ActiveTabIndex = N` setters.

### Fullscreen
- `WindowState.FullScreen` applied from `Opened`, re-applied from `Activated` when the state returns to `Normal`. Handles the taskbar-restore path.
- Rejected `WindowState="Maximized"` — Windows 11 DWM applies rounded corners to Maximized + ExtendClientArea windows, and the initial maximize was unreliable in Avalonia 11.0.x.

### Deferred to later phases
- Component patterns (buttons, inputs, grids, plot frames, modals) — Phase 3 of the design plan.
- Per-tab reskins using the tokens — Phase 3/4.
- Icons for the nav rail (currently text labels only).
- Theme color configuration via the Config tab (planned as a separate feature).
- Right cell of the status bar (active account + queue count) — blocked on wiring a lazy provider to avoid the Phase 17 startup-cost regression.

## Phase 20: Theme Color Configuration [COMPLETED]

Ship date: 2026-10-07.

Adds a user-editable theme palette to the Config tab. Each of the 14 design tokens introduced in Phase 19 is now backed by an `AppSettings` property, surfaced as a color swatch in a collapsed "Theme Colors" expander, and applied live to the shell on confirmation.

### Settings
- `AppSettings` gains 14 `Theme*Color` string properties (app bg, panel bg, elevated, sunken, text primary/secondary/disabled, border, accent, accent hover, accent pressed, semantic success/warn/danger). Defaults match the shipped palette.
- `ConfigManager.LoadConfig` seeds every property with `??=` so pre-theme `settings.json` files load cleanly.

### Applier
- `src/gui/Theme/ThemeApplier.cs` — static. Parses each `Theme*Color` with `Color.TryParse` and writes a `SolidColorBrush` into `Application.Current.Resources` for the matching token key. Silently skips unparseable values, so a bad hand-edit of `settings.json` degrades to the token default rather than throwing.
- Called from `App.axaml.cs` immediately after `ConfigManager` resolves, before `MainWindow` constructs. Called again from `ConfigViewModel.ApplyThemePreview` on color-dialog OK.

### Config tab
- `SettingItem.IsColor` (existing) routes any `string` property whose name contains "Color" into `ConfigViewModel.ThemeSettings`. Everything else stays in `DynamicSettings`.
- `ConfigView.axaml` gains a collapsed `Expander` labelled "Theme Colors" between Engine Settings and Provider Order. Each row is a `Label` + a 60×28 swatch button (`ColorToBrushConverter` binds the `ColorValue` to a background).
- A "Reset to Defaults" button inside the expander restores the shipped palette in memory; Save Settings persists.

### Color picker
- `src/gui/Config/ColorPickerWindow.axaml` — modal `Window` built from the primitive controls (`ColorSpectrum`, `ColorSlider`, `ColorPreviewer`) rather than `ColorPicker` / `ColorView`.
- **Why not the standard controls:** `ColorPicker` (and its `ColorView` sibling) exhibited two reproducible failures in Avalonia 11.0.11 — the popup's inner content lazy-loads on first click, freezing the app for a beat; and when instantiated directly in a `Window` the templated content rendered transparent. Both are documented in Avalonia discussions #9128 and #9098.
- The dialog exposes: the ring spectrum, a numeric Brightness slider (0–100) that reaches pure black at 0, a preview swatch, a hex input (`#RRGGBB`), and a live RGB readout.
- Hex/spectrum/slider stay in sync via an `_syncing` guard in `ColorPickerWindow.axaml.cs`. Slider at 0 produces true black regardless of the hue-ring position.

### Live apply semantics
- On OK: `ConfigView.axaml.cs` writes `picker.Result` into the `SettingItem.ColorValue` and calls `ConfigViewModel.ApplyThemePreview(item)`, which re-runs `ThemeApplier.Apply` against the current settings. The shell recolors immediately.
- Persistence is still gated on the Save Settings button. Closing the app without Save reverts to the on-disk palette on next launch.
- This deliberately splits "preview" from "commit". The user sees the change live but has to opt into writing it to `settings.json`.

### Deferred
- Importing or exporting palettes.
- Preset theme packs (a named palette picker).
- Live-updating already-open plot backgrounds when the sunken color changes — ScottPlot surfaces are configured at render, so a plot theme change requires a tab reload.

## Phase 21: Providers Tab Async Refresh [COMPLETED]

Ship date: 2026-10-07.

Eliminates the multi-second UI freeze when opening the Providers tab. The tab used to synchronously scan `data/cache/` and parse every cache file's metadata on the UI thread from the `ProvidersViewModel` constructor. On a cache with dozens of large files that blocked the app for ~3 seconds.

### Change
- `ProvidersViewModel` ctor now fires `RefreshAsync` fire-and-forget. The tab renders immediately with `StatusText = "Scanning cache…"` and populates when the scan completes.
- The per-file reads run via `Task.Run(() => Parallel.ForEach(files, ...))`. `CacheManager.ReadCacheMetadata` fully parses JSON and enumerates every bar — independent per file, no shared state — so parallel-across-cores cuts wall time proportionally.
- A `lock` guards the shared `List<CacheMetadata>` accumulator.
- Aggregation and `ObservableCollection<ProviderGroupViewModel>` population stay on the UI thread via `ConfigureAwait(true)` on the outer await. That part is O(files) and cannot reintroduce the freeze.
- A `_refreshing` bool prevents overlapping refresh invocations.
- The status line and a `LogTag.Cache` Debug line both report the file count, group count, and elapsed ms.

### Measured result
- Before: ~3 s freeze on first click, UI thread blocked.
- After: tab interactive immediately; groups appear in roughly `wall / coreCount` (~500–800 ms on a typical 4–6 core CPU).

### Deferred
- Sidecar `.meta.json` files written alongside each cache `.json` in `SaveCacheAsync`. Would turn `ReadCacheMetadata` into a 1 KB read per file instead of a full JSON parse. Bigger change; revisit if the post-parallel wait becomes annoying.
- Same async treatment for the Data tab's `RefreshData` — tracked under Phase 18B.

## Phase 22A: csproj Hygiene [COMPLETED]

Ship date: 2026-10-08.

First sub-phase of the build-performance work. Two csproj flags changed, nothing else. Dropped the dev-loop build times substantially -- better than projected.

### Change
- `EmitCompilerGeneratedFiles` flipped from `true` to `false`. Source generators (CommunityToolkit.Mvvm, Avalonia XAML compiler, System.Text.Json contexts) still run and still emit into the compilation; their output is no longer written to `obj/generated` on every build. On this codebase that was hundreds of files per build. The disk writes also fed back into the incremental-build file-hash check and were invalidating the previous build's outputs, which is the larger effect.
- `RunAnalyzersDuringBuild` added, set to `false`. Analyzer passes (Avalonia.Analyzers, CommunityToolkit.Mvvm.Analyzers, .NET built-ins) now run only in the IDE background pass, not on every CLI build. Pre-commit verification re-enables them explicitly.
- Both flags carry an escape hatch: `-p:EmitCompilerGeneratedFiles=true` for source-generator debugging; `-p:RunAnalyzersDuringBuild=true` for the pre-commit build.

### Measured result
| Build | Before | After | Delta |
|---|---|---|---|
| Incremental `dotnet build` | 7.14s | 4.04s | −43% |
| `dotnet build --no-incremental` | 15.56s | 7.80s | −50% |

### Notes
- The remaining 4.04s incremental is dominated by the Avalonia 11.0.11 XAML compiler defeating incremental builds (Avalonia PR #17539, fixed in 11.3.x). That is Phase 23's scope.
- Pre-commit verification builds must use `-p:RunAnalyzersDuringBuild=true` to restore analyzer coverage. Any code that relies on analyzer-only diagnostics will pass silently under the dev-loop flag.

### Files touched
- `backs2.csproj` -- two `<PropertyGroup>` additions.

### Deferred
- Phase 22B (build protocol change in `AGENT_RULES.md` / `RULES.md`).
- Phase 22C (VS Code workspace settings -- `.vscode/settings.json` was already committed in a separate chore commit).
- Phase 22D (measure and close -- acceptance threshold met early).
- Phase 23 (Avalonia 11.3.x upgrade -- the real fix for the remaining 4s).

## Phase 24A: Data Tab Async Refresh [COMPLETED]

Ship date: 2026-10-08.

Same class of fix as Phase 21 (Providers tab). The Data tab was freezing the UI for several seconds on first click because `DataViewModel`'s constructor ran a synchronous metadata scan of `data/cache` on the UI thread: `Directory.GetFiles` + `CacheManager.ReadCacheMetadata` per file (full JSON parse + full-bar enumeration to compute `HasVolume`, `MinT`, `MaxT`).

### Change
- `DataManager.GetAvailableDataAsync(CancellationToken)` added. Runs the file collection + per-file `ReadCacheMetadata` under `Parallel.For` inside a `Task.Run`, then builds the `DataEntry` list on the thread pool. The synchronous `GetAvailableData()` is retained for callers already off the UI thread.
- `DataViewModel` ctor now fires `RefreshData` fire-and-forget. The tab renders immediately with `Scanning cache…` and populates when the scan completes.
- Latest-wins: a `CancellationTokenSource?` field is stored per refresh. A new refresh cancels the previous one's token. `Parallel.For` receives the token via `ParallelOptions.CancellationToken`, and the token is checked after the parallel loop and again before touching `_all`.
- `FetchInternal` awaits `RefreshData` after a successful fetch, so the just-written file is visible before the provider is resolved from cache metadata. This await also cancels any in-flight ctor refresh (correct — the fetch reflects newer state).

### Why `Parallel.For` and not `Parallel.ForEach`
`Parallel.For` with an index lets the results go into a pre-sized `CacheMetadata?[]` array. `Parallel.ForEach` would need a locked accumulator. Both work; the indexed form avoids the lock.

### Result
- Before: UI thread blocked for seconds on first Data-tab click; the tab was unusable until the scan finished.
- After: tab interactive immediately; grid populates within roughly `wall / coreCount`.

### Not changed
- `DataEntry` shape, cache file format, and `ReadCacheMetadata`'s single-pass enumeration are all unchanged.
- `GetCachedSymbols` and `GetCachedTimeframes` still use the synchronous `ReadCacheMetadata` path. Neither is called from a UI-facing ctor today; revisit if that changes.


## Phase 26A: Version Data Model and Storage [COMPLETED]

Ship date: 2026-10-08.

First sub-phase of the parameter optimizer. Inert on its own: establishes the on-disk schema and the store class that the execution pipeline (26B) writes into. Nothing consumes it yet.

### Records
- `src/core/Optimizer/OptimizationBatch.cs`:
  - `ParameterSet(Name, Type, Value)` - one grid coordinate tuple.
  - `McSummary(...)` - 23 scalars + 3 ints, a subset of `MonteCarloResult` (per-simulation arrays deliberately excluded).
  - `OptimizationVersion(...)` - grid coordinate, backtest scalars, optional `McSummary`, `TerminalBalances[]` (1000 doubles = 8 KB), `RepresentativeCurve[]` (1000 doubles = 8 KB, downsampled from the median-terminal-balance simulation), `CurvesRetained` flag.
  - `OptimizationBatch(...)` - batch metadata (strategy, dataset, MC config, account rules) and `OptimizationVersion[]`.
- `src/core/Optimizer/OptimizationJsonContext.cs` - source-generated context mirroring `KlineJsonContext` / `AccountJsonContext`. Camel-case keys, nulls omitted (an unrun MC pass does not emit a null `Mc` block).
- `src/core/Optimizer/OptimizationStore.cs` - save/load/list against `data/optimizations/`. Directory auto-created on construction. `SaveAsync` writes one file per batch at `batch_{Id}.json`; Id collisions resolved with a numeric suffix. `LoadAsync` reads one file. `ListBatchFiles` enumerates newest-first, no parsing.

### Storage budget
Per version: parameter dict + backtest scalars + MC scalars + two 1000-element arrays = approx 20 KB. 500-version batch = approx 10 MB. Well under the 15 MB budget in the roadmap entry.

### Wiring
- `OptimizationStore` registered as a singleton in `App.axaml.cs` alongside `CacheManager`, `PresetService`, `AccountService`.
- `.gitignore` gains `data/optimizations` next to `data/cache` - engine-managed, same class of directory.

### Not in this phase
No grid generation, no execution, no UI. The store exists but nothing calls it.

---

## Phase 26B.1: StrategyManager Parallelism Verification [COMPLETED - PASSED]

Ship date: 2026-10-08.

Gates the entire 26B execution pipeline: if the Roslyn-compiled `ScriptRunner` were not safe for concurrent invocation, the optimizer would be stuck running backtests serially and would have to lean entirely on the two-pass MC strategy for speed. The verification answered this in the affirmative.

### Test
- `Program.cs` gains `--verify-strategy-parallelism`. AttachConsole P/Invoke added so `Console.WriteLine` from the WinExe process lands in the launching terminal (the original attempt was silent because WinExe projects do not inherit a parent console on Windows).
- Picks the first strategy in `data/strategies/` that emits at least 3 trades on synthetic random-walk data. `HelloVwap.cs` produces 0 trades on that data and was rejected as a candidate - a 0-trade result matches trivially and proves nothing. `ParamTest.cs` produced 10 trades.
- Runs the chosen strategy 4 times serially with `SerializeBacktests = true` (semaphore in place) and captures a stable signature over the trade list (side, entry/exit index, entry/exit price). Then runs it 4 times concurrently with `SerializeBacktests = false` (throttle bypassed) via `Task.WhenAll`.

### Result
[verify] candidate HelloVwap.cs: 0 trade(s)
[verify] candidate ParamTest.cs: 10 trade(s)
[verify] chosen strategy: ParamTest.cs (10 trade(s), 5 param(s) defaulted)
[verify] baseline: 10 trade(s), sig=-7764140367496042945
[verify] serial: 4/4 match baseline
[verify] parallel: 4/4 match baseline
[verify] PASS

### Consequence
- `StrategyManager.SerializeBacktests` added as a public bool (default `true` for the single-run UI path; set to `false` by the optimizer).
- Version-level parallelism is available to 26B. Expected speedup on the search phase: up to `Environment.ProcessorCount` x on backtest-heavy grids.
- `StrategyManager.RunBacktestFromFileAsync` only takes the semaphore when `SerializeBacktests` is true. The change is backward-compatible - no existing call site needs to know about the flag.

### Rollback path
If a future strategy demonstrates a concurrency regression (compiled scripts are per-file cached, and a script that captures mutable state at compile time could theoretically diverge), flip `SerializeBacktests` back to `true` in the optimizer path and fall back to the two-pass MC strategy for speed. The verification flag (`--verify-strategy-parallelism`) remains as a regression canary.

---

## Phase 28B: Parent-Process Hybrid Scheduler [COMPLETED]

Ship date: 2026-10-10. Shipped after 28A (compute abstractions + parent dispatcher).

Scheduler for the `src/core/Computing/` subsystem. Phase 28B as originally written scoped this "in the worker", but Compute Worker Phase 3 is not a scheduled phase and does not exist, so parking the subsystem was the wrong trade. The scheduler ships in the parent process against the DI-resolved backend; the worker migration is deferred to a later phase. `IComputeDispatcher`, `IComputeBackend`, and the dispatcher's public signatures do not change when that move happens.

### Scheduler
- `CpuComputeDispatcher.DispatchMonteCarloAsync` now schedules instead of passing through: `iterations` is cut into `McChunkSize` (10,000) iteration chunks, one chunk is one unit of work.
- Work-stealing is a `ConcurrentQueue<int>` of chunk indices and `Parallel.For(0, workerCount, ...)`; each worker loops on `TryDequeue` until the queue is dry. `workerCount = Math.Min(Environment.ProcessorCount, chunkCount)`, and a single-chunk batch runs `1` worker so small dispatches never pay ThreadPool overhead.
- `IComputeBackend` gains `RunIterationsAsync(slices, targetDurationSlices, startIteration, iterationCount, seed, ct)`. The scheduler owns chunking, work-stealing and result placement; the backend owns how a contiguous iteration range executes. `CpuBackend` implements it as a sequential loop on the calling worker (the scheduler is the parallelism, so a nested `Parallel.For` would over-subscribe). `GpuBackend` implements it as a delegated passthrough to its CPU fallback -- the deliberate single change point for the Phase 29B `[Shader]`. **Superseded by 29B** (entry at the top of the 29x block, above 29A): the return shape is no longer `Task<double[]>`. `IComputeBackend.RunIterationsAsync` and `RunSimulationsAsync` now take `MonteCarloSimulationRequest` plus `retainCurves` / `maxDegreeOfParallelism`, and both return `MonteCarloDispatchResult` -- nine parallel per-iteration arrays (`TerminalBalances`, `Outcomes`, `SlicesElapsed`, `MaxDrawdowns`, `BestDayRatios`, `LongestLosingStreaks`, `LongestFlatStretches`, `RuinCauses`) plus optional `EquityCurves` -- because `BuildResult` aggregates all nine and the retained-curve path needs the curves. The chunk queue, `McChunkSize` 10,000, the work-stealing loop, the absolute-index RNG split and the cancellation semantics are unchanged, so the scheduler described above is still accurate. `GpuBackend` is no longer a passthrough -- it holds a real DirectX 12 kernel.
- The dispatch loop is backend-agnostic: no caller-side branch decides CPU vs GPU, so chunks can be routed to `GpuBackend` once `IsAccelerated` becomes `true`.
- `DispatchBacktestBatchAsync` keeps one combination per unit of work and `Parallel.For` across the batch at `Environment.ProcessorCount`. Backtests are Roslyn executions -- non-vectorizable, not chunkable -- and never enter the scheduler queue.
- Chunk-dispatch decisions log at Debug under `LogTag.MonteCarlo` (one line for the schedule, one per completed chunk with its absolute iteration range). No new tag, no new package, no IPC dependency.

### Determinism
- The RNG for iteration `i` is `new Random(seed + i)` against the **absolute** index in `[0, iterations)`, never the chunk-local index, and the absolute index is also what reaches `ISimulationKernel.SimulateOnce`. This matches `MonteCarloEngine.Run`'s existing split, so results are stable when `McChunkSize` or the core count changes.
- Verified out of process (reflection probe against the built `backs2.dll`, same pattern as the 28A verification): `CpuBackend.RunSimulationsAsync(slices, 100, 25_000, seed: 42)` and `CpuComputeDispatcher.DispatchMonteCarloAsync(slices, 100, 25_000, 42)` produced `SequenceEqual == true`, and the chunked call repeated with the same seed was identical. 25,000 iterations scheduled as exactly 3 chunks (10k + 10k + 5k).

### Boundary behaviour
- Cancellation returns the chunks that completed as a truncated `double[]` and never throws. A chunk is published only once it is demonstrably whole (`chunkResults.Length == iterationCount`), so a cancelled result is a sum of completed chunks (0/5k/10k/15k/20k/25k for a 25k batch) instead of an arbitrary iteration cut. Measured: 250,000 iterations full in ~61 ms, cancelled at 40 ms returned 190,000 = 19 whole chunks.
- Degenerate input fails soft: `iterations == 0`, empty slices, non-positive `targetDurationSlices`, and a null request all return an empty array. `iterations < McChunkSize` is one chunk with no parallelism overhead; a non-divisible count leaves a short final chunk.
- `seed == null` uses `Random.Shared`, matching `CpuBackend`.

### Consequence
- The chunked path is the default for `IComputeDispatcher` callers, and it is bit-identical to the whole-batch backend call, so 29B can retrofit `MonteCarloEngine.SimulateOnce` onto the dispatcher without a determinism story to re-derive.
- The remaining worker migration is a wiring change behind `IComputeDispatcher`: the loop, the chunk size, and the backend seam do not move.

### Rollback path
`CpuComputeDispatcher.DispatchMonteCarloAsync` can return to a direct `_backend.RunSimulationsAsync` passthrough by deleting the chunk loop; `RunIterationsAsync` stays as an unused backend seam. Nothing else depends on the scheduler.

---

## Phase 28A: Abstractions & Parent Dispatcher [COMPLETED]

Ship date: 2026-10-10.

Bootstrap of the `src/core/Computing/` subsystem: the parent-process facade, the backend seam, and the null-object kernel placeholder that keeps the layer resolvable before the real Monte Carlo state machine exists. Phase 28B kept every published signature from this phase intact and replaced the pass-through body of `DispatchMonteCarloAsync` with the chunked scheduler, so the surface frozen here is the surface callers use today.

### Delivered
- `src/core/Computing/IComputeDispatcher.cs` -- `IComputeDispatcher` with `Task<double[]> DispatchMonteCarloAsync(MonteCarloDispatchRequest request, CancellationToken ct)` and `Task<double[]> DispatchBacktestBatchAsync(int jobCount, CancellationToken ct)`. Domain-typed on purpose: callers ask for a Monte Carlo dispatch or a backtest batch, never for a CUDA call. The interface carries no reference to `WorkerIpc`, `WorkerEnvelope`, or `JobConfig`.
- `MonteCarloDispatchRequest(TimeSlice[] Slices, int TargetDurationSlices, int Iterations, int? Seed = null)` -- a record rather than a parameter list, so the same shape can cross the worker IPC boundary as JSON later without reshaping the surface. **Superseded by 29B** (entry at the top of the 29x block, above 29A): the record now carries `Simulation` (`MonteCarloSimulationRequest`), `Slices`, `Iterations`, `Seed`, `RetainCurves` and `MaxDegreeOfParallelism`, and both `IComputeDispatcher.DispatchMonteCarloAsync` and the `IComputeBackend` methods return `MonteCarloDispatchResult` instead of `double[]`. The JSON-shaped-record rationale and the absence of any `WorkerIpc` / `WorkerEnvelope` / `JobConfig` reference still hold.
- `src/core/Computing/CpuComputeDispatcher.cs` -- thin in-process implementation that resolves `IComputeBackend` from DI and delegates. 28A held no compute logic of its own: `DispatchMonteCarloAsync` forwarded to `IComputeBackend.RunSimulationsAsync` after a Debug log, and `DispatchBacktestBatchAsync` ran a `Parallel.For` skeleton at `Environment.ProcessorCount` that wrote `scores[i] = i`.
- `src/core/Computing/Backends/IComputeBackend.cs` -- the Phase 27 signature, `RunSimulationsAsync(slices, targetDurationSlices, iterations, seed, ct)`, plus `Name` / `IsAccelerated`. Contract: degenerate input returns empty instead of throwing, cancellation is observed between iterations, and the result array is `max(iterations, 0)` long and indexed by iteration.
- `src/core/Computing/Backends/CpuBackend.cs` -- `Parallel.For` over iterations at `Environment.ProcessorCount` (one iteration runs inline; ThreadPool dispatch costs more than the work), with a single-iteration fast path and RNGs split as `new Random(seed + i)` (`Random.Shared` when unseeded), exactly as `MonteCarloEngine.Run` does it.
- `src/core/Computing/Backends/GpuBackend.cs` -- ComputeSharp DirectX 12 device probe. A failed probe logs one `Warn` and stores a `CpuBackend` fallback instead of throwing; `Name` reports `GPU (<adapter>)` once a device is held, and `IsAccelerated` stays `false` until a real `[Shader]` exists.
- `src/core/Computing/Backends/ISimulationKernel.cs` -- the per-iteration seam `SimulateOnce(slices, targetDurationSlices, rng, iteration)` that separates the parallel skeleton from the Monte Carlo state machine. Backed by a private non-throwing `PlaceholderKernel` until Phase 29B ports the real engine.
- `ComputeSharp` 3.2.0 `PackageReference` in `backs2.csproj` (net8.0, source-generator kernels, no runtime compilation step). Restore verified; no new `LogTag`.

### DI wiring
- `App.axaml.cs` registers `IComputeBackend` -> `CpuBackend` and `IComputeDispatcher` -> `CpuComputeDispatcher` as singletons.
- `GpuBackend` is deliberately not registered: the parent UI process must not hold a GPU device. It is resolved inside the worker, which is where holding one is correct. **Superseded by 29B** (entry at the top of the 29x block, above 29A): `CpuBackend` and `GpuBackend` are both registered in the parent process, `CpuComputeDispatcher` resolves the accelerated one, and a DirectX 12 device is held in process -- the worker migration is still deferred.

### Scope changes against the original entry
- **The parent-side `WorkerEnvelope` proxy did not ship.** 28A was written to proxy dummy requests to the worker through `ComputeWorkerManager`, but Compute Worker Phase 3 is not a scheduled phase and does not exist, so there was no IPC path to proxy over. The entry therefore closes with no `WorkerEnvelope` / `ComputeWorkerManager` reference anywhere in `src/core/Computing/`.
- **The "Compute Worker Phase 3 complete" precondition was dropped.** It gated a phase on work that was never scheduled. `Phase 26 closed` stands on its own, and `MonteCarloDispatchRequest` keeps the payload JSON-shaped so a future proxy can be added without touching callers.
- 28A sized the dispatcher as a pass-through and expected 28B to be the worker-IPC proxy. 28B instead parked the worker migration and shipped the chunked scheduler in the parent process against the DI-resolved backend (see the Phase 28B entry above); `IComputeDispatcher`, `IComputeBackend`, and the dispatcher's public signatures were unchanged by that swap.

### Consequence
- The compute layer is exercisable end to end in-process: a caller can dispatch a real Monte Carlo batch and get `double[]` terminal balances back with no worker, no pipe, and no GPU.
- 28B extended this surface rather than redesigning it -- a chunk-level `RunIterationsAsync` was added to `IComputeBackend` and the MonteCarlo body became the work-stealing scheduler. Nothing documented above had to change.

### Rollback path
`CpuComputeDispatcher` was a pure delegation to `IComputeBackend.RunSimulationsAsync`, so the bootstrap is removable without touching callers: drop the DI registrations and the subsystem stops being reachable. 28B has since made the dispatcher load-bearing for the chunked path, so the live rollback is the 28B one (return `DispatchMonteCarloAsync` to whole-batch), not this.

## Phase 29A: Instrumentation & Bottleneck Verification [COMPLETED]

Ship date: 2026-10-10. Confirmation step ahead of the Phase 29B/29C retrofits.

Instrumentation only, no optimization. `Stopwatch` markers were added around every stage of `OptimizationRunner.RunAsync` so the wall-time split Phase 22E could only hypothesize about is now a measured number. The loops, the work, and the parallelism are unchanged; the runner just records where its wall time goes and logs it.

Measured on a 20-core machine, 500 combinations (`OptimizerMaxCombinations` 500, sampled from an `EtWindow` grid of 1,681), `OptimizerCoarseIterations` 100, `OptimizerRefineIterations` 1000, `OptimizerRefineTopN` 20, 20 survivors refined, `GOLD.pro@30m`. Two full Optimize passes were taken. Run 2 is the quote. Run 1 is the cold-start run (first Roslyn execution, cold page cache) and is kept because the spread is itself part of the finding; it was taken one instrumentation revision earlier, before the backtest phase got its own wall span, so its backtest row is reconstructed from the `backtests complete in` log line rather than read from a `[29A]` line.

### Numbers

Wall time per stage. Percentages are of that run's total wall; a stage's percentage is its share of the critical path.

| Stage | Run 1 cold (ms) | % | Run 2 warm (ms) | % |
|---|---|---|---|---|
| Setup (grid + topology + ETA) | 4 | 0.0% | 5 | 0.1% |
| Backtest phase (wall) | 7,495 | 81.1% | 3,583 | 66.0% |
| Coarse MC phase (wall) | 761 | 8.2% | 804 | 14.8% |
| Refine MC phase (wall) | 885 | 9.6% | 933 | 17.2% |
| Save/persist | 83 | 0.9% | 89 | 1.6% |
| Phase coverage | 9,228 (99.9%) | | 5,414 (99.8%) | |
| **Total wall** | **9,238** | | **5,428** | |

Thread-summed per-stage work (summed across all 20 workers, so above 100% is expected and just means "this stage used more than one core"):

| Stage | Run 2 thread time (ms) | % of wall | Invocations |
|---|---|---|---|
| Backtest, per-version span | 71,197 | 1,311.6% | 500 |
| MonteCarloEngine.Run (coarse + refine) | 27,806 | 512.2% | 280 |
| `TimeSliceBuilder.Build` | 4,438 | 81.8% | 280 |
| `StrategyManager` overhead (derived) | 38 | 0.7% | 500 |
| `NullModelGenerator.GenerateRandomSignalTrades` | 21 | 0.4% | 20 |
| Coarse version span (skips + slices + MC) | 14,601 | 269.1% | 500 |
| Refine version span | 17,902 | 329.9% | 20 |

Thread-summed work totals 103,500 ms (run 2) against a 20-core ceiling of 20 x 5,428 = 108,560 ms, and 179,715 ms (run 1) against 20 x 9,238 = 184,760 ms. That is **95.3% and 97.3% occupancy** -- the machine is essentially pinned for the whole run, in both a cold and a warm pass. The occupancy is not an artifact of one outlier phase: the backtest phase alone accounts for 71,197 ms of run 2's 103,500 ms.

### Verdict: compute-bound, not I/O-bound

Phase 22E's hypothesis was that Optimize runs at 22% average CPU because it spends most of its wall time *waiting* -- "on disk I/O, on `StrategyManager`'s compile cache, on MC curve allocation, or on a serialized stage." **Every one of those four is wrong.**

- **Disk I/O and the compile cache are not the problem.** Everything `StrategyManager` does per version outside the script -- file existence check, `FileInfo` stat, compile-cache lookup in `GetOrCreateRunner`, source read -- is a 76 ms (run 1) / 38 ms (run 2) derived residual across 500 versions. That is 0.04-0.15 ms per version, and under 1% of wall. The cache hits as designed, and the one real Roslyn compile in the run is amortized to nothing across 500 calls.
- **The coarse MC loop is not a serialized bottleneck.** It owns 8-15% of wall time. The prime suspect named by the 22E follow-up was not where the wall time goes.
- **The 22% CPU average is not reproducible here.** Two runs show 95.3% and 97.3% of a 20-core ceiling in thread-summed work. The 2026-10-09 `dotnet-counters` capture that produced 22% is contradicted; the likely explanation is the profiler artifact Phase 22E already documented in the neighbouring warning (a shared writer serializing parallel MC events), not idle wall time. That capture should not be cited again as evidence of what Optimize waits on.
- **What actually costs wall time is the backtest phase**: 66-81%, with `EtWindow` at 500/500 combinations. It is 20-way parallel across combinations and still consumes its full share of the machine, so the cost is the single-threaded Roslyn script execution per combination, not a scheduling gap.

### Implication for 29B and 29C

- **29C (backtest retrofit) is the phase with the wall-time lever, and it has less headroom than it looks.** Per-combination work is already one unit of work per script execution, and `Parallel.ForEachAsync` already runs it 20-wide within roughly 95-99% of what the phase can consume. Moving that loop behind `IComputeDispatcher` and into the worker is therefore a *placement* change -- process isolation, a memory cap, and an unblocked UI thread -- not a throughput change. Worth doing for those reasons; not worth doing in the expectation that it shortens a 500-combination run.
- **29B (MC retrofit) still applies, with a caveat.** Monte Carlo is 27.8 s of run 2's 103.5 s of thread time, just over a quarter and is already spread 20-wide across the inner degree, so it is thread-time that a GPU can absorb rather than a serialized wall-time stall waiting to be removed. It is the right target for the eventual `[Shader]` path; it is not the fix for the wall-clock distribution measured here.
- **Neither retrofit is gated on a serialization problem, because there is no serialization problem to fix.** The remaining critical path is a CPU-bound parallel loop. If the goal is a shorter Optimize run, the lever is the cost of one script execution, not the concurrency topology.

### Instrumentation shipped

- One `Stopwatch` for total wall time, plus `Stopwatch.GetTimestamp()` + `Stopwatch.GetElapsedTime()` (or the raw tick delta) around each instrumented region. Per-version spans are accumulated with `Interlocked.Add` into `long` tick fields -- one atomic add per region, no allocation, and no per-iteration logging.
- One `LogManager.Info(LogTag.Runner, ...)` line per stage at the end of the run: stage name, accumulated ms, percent of total wall, and invocation count. A final line carries total wall ms, `Environment.ProcessorCount`, combination count, and survivor count. Every line is prefixed `[29A]`, so the whole block extracts with one `Select-String '\[29A\]'` against `logs/runtime-*.log`. No new `LogTag`, no new package.
- The summary is emitted from a `finally` around the run body, so a cancelled run logs whatever accumulated before the cancel. Percent is `n/a` rather than a division by zero when total wall time is zero.
- The `StrategyManager` compile-cache lookup is the one **derived** number rather than a wrapped span: total call time minus script execution (`StrategyRunResult.Duration`) leaves everything `StrategyManager` does around the script, and the cache lookup and the source read are not separable from outside that class. `StrategyManager.cs` was out of scope for this phase, so the lookup is measured, not wrapped, and the line is labelled as derived.
- The instrumentation ships on -- there is no enable/disable flag. It is scaffolding for 29B/29C and a future phase removes it or downgrades it to Debug. Accumulators are per-instance and the runner is a DI singleton, so `RunAsync` resets them at entry; that is safe because the UI runs one Optimize pass at a time behind `IsBusy`.

## Phase 29B: Monte Carlo GPU Retrofit [COMPLETED]

Ship date: 2026-10-10 (`f9d5dab`). Replaces the Phase 27 GPU plan; the retrofit that Phase 29A sized and Phase 28B made hookable.

Phase 29B ports the Monte Carlo state machine onto the `src/core/Computing/` subsystem and gives `GpuBackend` a real DirectX 12 kernel. `MonteCarloEngine.SimulateOnce` moved verbatim into `SimulationKernelCpu`; the engine now owns only parameter derivation, the `MaxIterations` clamp, the `MaxCurveCells` ceiling, aggregation and `BuildResult`, and it dispatches through `IComputeDispatcher`. Phase 27 assumed the GPU would live in the Compute Worker process behind IPC; it does not, because for in-process Monte Carlo there is no IPC boundary to protect and holding the device where the workload runs is simpler. The worker migration stays deferred.

### Delivered

- `src/core/Computing/Backends/SimulationKernelGpu.cs` -- the ComputeSharp `[Shader]` kernel: one GPU thread per simulation, one dispatch per chunk, reproducing the CPU state machine statement for statement and in the same order (MTM-low vs daily floor, max-DD floor, realize PnL, HWM update, streak/drawdown bookkeeping, EOD reset). RNG is Philox-4x32-10 seeded per thread from `(seed, absolute iteration)`: `System.Random` cannot exist in a shader, the 64-bit Philox products are built from 16-bit partial products, and the state is plain `uint` scalars because ComputeSharp 3.2.0 exposes no HLSL vector types to shader code. `ThreadsPerGroup = 64`.
- `src/core/Computing/Backends/SimulationKernelCpu.cs` -- the `SimulateOnce` that shipped through 29A, moved here unchanged, now reachable through `ISimulationKernel.Simulate(...)`. Both backends share one stateless instance.
- `src/core/Computing/Backends/GpuBackend.cs` -- real dispatch. The constructor probes `GraphicsDevice.GetDefault()`, never throws, and keeps the kernel only when the device is hardware-accelerated *and* kernel creation succeeds; a software adapter or a kernel-creation failure logs one Warn and leaves `IsAccelerated = false` with the `CpuBackend` fallback carrying every call. A chunk that fails on the device re-runs on the CPU for the same absolute iteration range. `GpuIterationBatch = 10_000`, matching the scheduler's `McChunkSize`, so a scheduler call is normally one dispatch; the slice array is uploaded once per call and reused across dispatches.
- `src/core/Computing/Backends/IComputeBackend.cs`, `ISimulationKernel.cs`, `CpuBackend.cs`, `src/core/Computing/IComputeDispatcher.cs`, `CpuComputeDispatcher.cs` -- the contract widened from `Task<double[]>` (terminal balances only) to `Task<MonteCarloDispatchResult>` (see the 28B `Superseded by 29B` note below). `MonteCarloSimulationRequest` carries the per-batch state machine parameters (pre-scaled floors, target, block size, drawdown type) so the CPU and GPU kernels take the identical shape; the GPU side consumes it as a uniform constant buffer, so its field order and types are an ABI. `retainCurves` and `maxDegreeOfParallelism` travel end to end, and `CpuComputeDispatcher` resolves the accelerated backend so no caller names a device.
- `App.axaml.cs` -- registers `SimulationKernelCpu`, `CpuBackend`, `GpuBackend`, both `IComputeBackend` registrations, and `IComputeDispatcher`. The dispatcher picks the accelerated backend when the probe succeeded.
- `backs2.csproj` -- `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`, required by ComputeSharp's `[GeneratedComputeShaderDescriptor]` source generator (CMPS0052).

### Measurement

Workload: the Phase 29A configuration -- `EtWindow` on `GOLD.pro@30m`, 5,000 capital, static drawdown, 8% max loss, 4% daily loss, no profit target, block 5, 240 versions that actually run MC, 20 cores, `NVIDIA GeForce RTX 4060 Laptop GPU` (3,072 compute units, 8 GB). Coarse = 240 x 100 iterations; refine = 20 x 1,000 iterations. All figures below are from `f9d5dab`'s commit report.

| Pass | CPU warm (ms) | GPU (ms) | Winner |
|---|---|---|---|
| Coarse MC, 240 x 100 iterations | 261-487 | 2,307-2,361 | CPU, by ~5x |
| Refine MC, 20 x 1,000 iterations | 112-321 | 89-109 | GPU (narrowest comparable pass 92 ms GPU vs 114 ms CPU) |

Per-dispatch cost inside the coarse pass: **~0.1 ms of kernel work against ~10 ms of slice mirroring, upload, readback and nine output-buffer allocations**. That puts the kernel share of a single dispatch at roughly `0.1 / 10 = 1%`, and against the measured GPU coarse wall (~2.3 s for 240 dispatches) at `0.1 / 2300 ≈ 0.004%` per dispatch. `f9d5dab`'s report states the phase-level split as **4.4% kernel work / 95.6% transfer and setup** (`0.1 / 2.3` per dispatch, `100 - 4.4 = 95.6`); that phase-level split is **not independently reconstructible** from the numbers in this entry -- the commit does not say whether it is thread time or wall time, and nothing in `logs/runtime-*.log` carries per-dispatch timing -- so it is recorded here as reported, `n/a -- not independently derivable`. What the numbers do support is the direction and the size: the fixed cost that surrounds each 100-iteration call dominates the kernel by orders of magnitude. The loss is structural, not a tuning gap -- the coarse pass is 240 separate tiny calls, and each one pays the whole fixed cost.

Single-dispatch throughput, where that fixed cost does amortize:

| Iterations per dispatch | GPU (ms) | CPU (ms) | Speedup |
|---|---|---|---|
| 1,000 | 21-26 | 45-56 | ~2x |
| 10,000 | 42-51 | 426-477 | ~10x |
| 100,000 | 480-519 | 6,392-6,714 | ~13-16x |

So the kernel is **10-16x faster per iteration** and the workload shape is what decides the phase.

Equivalence: GPU run-to-run identical for the same seed on 8/8 seeds across all nine output arrays; CPU vs GPU on 10,000 iterations x 1,796 slices equivalent for P(ruin), P(funded) and median terminal balance within 3 standard errors on 8/8 seeds. The GPU's seed-to-seed spread is the same size as the CPU-vs-GPU difference, so the residual is sampling noise, not a systematic bias.

### Verdict

**GPU loses on this workload as dispatched; the kernel itself wins; batching is the fix.**

This phase's acceptance criteria explicitly treated "GPU loses" as a valid outcome -- the phase was scoped to port the kernel and measure it honestly, not to make the optimizer faster. The retrofit succeeded on its own terms: the kernel reproduces the state machine, the two backends agree statistically, the fallback is safe, and the phase measured a real structural limit instead of assuming one away. The coarse pass is 240 tiny dispatches, each of which pays ~10 ms of mirroring, upload, readback and output-buffer allocation against ~0.1 ms of kernel work; at 100 iterations a dispatch is far below the size where the device pays for itself. The refine pass, at 1,000 iterations per call, already flips to a GPU win, and a single 100,000-iteration dispatch wins by 13-16x. **Batching the coarse MC dispatches is the fix**, not a kernel change.

### Caveats

- **The 29A slice arrays were not reconstructible**, so the CPU-vs-GPU delta is like-for-like (same machine, same session, same slices, same parameters on both sides) but the absolute ms are **not** the archived 804 ms coarse / 933 ms refine baseline. The archived refine wall also contains the concurrent null pass, which makes 933 ms not a like-for-like refine baseline either. Absolute numbers in the tables above are this phase's own measurement, not the 29A archive.
- **The 29A configuration is 100% timeout** (P(ruin) = P(funded) = 0 on both sides), so it populates no tail outcome and would make an equivalence check vacuous. The equivalence pass therefore widens the floors and adds a reachable profit target to populate both tails. The equivalence claim is made on that widened configuration, not on the 29A settings.
- **`AllowUnsafeBlocks` was needed** -- ComputeSharp's `[GeneratedComputeShaderDescriptor]` source generator emits pointer-based interop for every `[Shader]` kernel (CMPS0052). It is on for the whole project, not scoped to the kernel file.
- The two backends are **not bit-identical**: `System.Random` cannot exist in a shader, so the CPU stream and the Philox stream differ. The contract is statistical equivalence of aggregates plus run-to-run determinism *within* a backend. Chunk boundaries cannot move a result, because the RNG is derived from the absolute iteration index.
- No timing instrumentation ships with this phase. The numbers above come from the harness's own passes; there is no `[29B]` log block, so a future run cannot reproduce them from `logs/runtime-*.log` alone. The single-dispatch throughput rows were measured out of process and are **not** captured in the runtime logs (`n/a -- not captured` there).
- The GPU figures are one adapter on one machine (RTX 4060 Laptop, 8 GB). The verdict is about dispatch shape, which is adapter-independent; the absolute throughput is not.

### Follow-up

**Phase 29B.1 -- batch the Monte Carlo dispatches.** The optimizer's coarse pass issues one `MonteCarloEngine.Run` per version; batching those 240 x 100-iteration calls into dispatches of ~10,000 iterations is what lets the GPU path beat the CPU on the current optimizer workload. It requires restructuring the optimizer loop to collect a batch before dispatching, and it is **not yet scheduled**. It is a decision, not a commitment, and it is deliberately not specced as a numbered ROADMAP block. Until it lands, the CPU backend is the correct resolved backend for the coarse pass on this workload.

### Rollback path

Revert `f9d5dab`. The change is confined to `src/core/Computing/` (backends, dispatcher, result shape), `src/core/Overview/MonteCarloEngine.cs`, `App.axaml.cs`, and one `backs2.csproj` property, with no schema, persisted state, config key, or file format involved and no migration in either direction. Reverting restores `GpuBackend` as a probe plus a CPU delegation whose `IsAccelerated` stays `false`, so the app keeps working on CPU exactly as it did through Phase 28B. `git checkout 4dc2abf -- src/core/Computing/ src/core/Overview/MonteCarloEngine.cs App.axaml.cs backs2.csproj` restores the pre-29B surface. No data, cache, or log format changed.

## Phase 30: Roslyn Backtest Cost Investigation [COMPLETED]

Ship date: 2026-10-10. Discovery step ahead of any backtest optimization.

Instrumentation only, no optimization. 29A proved the Optimize wall time lives in the backtest phase and reduced everything `StrategyManager` does *around* the script to a 38 ms derived residual -- which left ~3,545 ms of the warm backtest phase attributed to "`ScriptRunner` compile + execute" as a single unmeasured number. That number was the largest unknown on the critical path. This phase splits it by wrapping the stages *inside* `StrategyManager`, which 29A was explicitly forbidden to touch.

Workload is 29A's, unchanged: 500 combinations (`OptimizerMaxCombinations` 500, sampled with seed 42 from an `EtWindow` grid of 3,362 = Side 2 x StartMinute 41 x EndMinute 41), `OptimizerCoarseIterations` 100, `OptimizerRefineIterations` 1000, `OptimizerRefineTopN` 20, `GOLD.pro@30m` (60,540 bars), 20 cores. Two passes were taken in one process: pass 1 cold (first Roslyn use in the process), pass 2 warm. The Phase 30 replay produced the same 500 parameter sets in the same order with the same trade counts and net PnL as the archived 29A batch `batch_20261010-161522.json` -- verified element-by-element across all 500 versions, 0 mismatches -- so the measurement is on the identical workload.

### Numbers

Per-version stage accumulators, thread-summed across the 20 backtest workers. Percentages are of the backtest phase's own thread-summed span, which is the span the split has to explain. A stage can exceed 100% of the backtest *wall* because the workers are concurrent; that overshoot is the parallelism, not measurement error.

| Stage | Cold pass (ms) | % | Warm pass (ms) | % |
|---|---|---|---|---|
| Backtest phase wall | 7,610 | -- | 1,081 | -- |
| Backtest phase span, thread-summed | 151,663 | | 21,209 | |
| Compile (`CSharpScript.Create` + `CreateDelegate`) | 126,284 | 83.3% | 0 | 0.0% |
| Invoke (`runner(globals)`) | 23,535 | 15.5% | 19,796 | 93.3% |
| Source read (`File.ReadAllTextAsync`) | 1,054 | 0.7% | 874 | 4.1% |
| Stat (`File.Exists` + `FileInfo`) | 249 | 0.2% | 320 | 1.5% |
| Globals setup (`TradeRecorder` + `RunnerScriptGlobals`) | 5 | 0.0% | 1 | 0.0% |
| Cache lookup (key + dictionary) | 1 | 0.0% | 1 | 0.0% |
| Unwrapped residual | 535 | 0.4% | 217 | 1.0% |
| Cache hits / misses | 480 / 20 | | 500 / 0 | |
| Real compiles | 20 | | 0 | |
| Invoke per version | 47.07 ms | | 39.59 ms | |

Whole-run context for the two passes:

| Metric | Cold pass | Warm pass |
|---|---|---|
| Total wall | 9,403 ms | 3,255 ms |
| Backtest phase wall | 7,610 ms (80.9% of wall) | 1,081 ms (33.2% of wall) |
| Coarse MC phase wall | 824 ms (8.8%) | 826 ms (25.4%) |
| Refine MC phase wall | 876 ms (9.3%) | 1,308 ms (40.2%) |
| Save/persist | 73 ms (0.8%) | 39 ms (1.2%) |

### Attribution probes

The invoke number is only actionable if it can be split into "the strategy's own work" and "Roslyn plumbing", so two probes were run in the same process after the passes. Both are plain C# with no Roslyn, no globals and no delegate; neither is part of the shipped execution path.

- **Conversion probe.** The `EtWindow` body calls `EasternTime.ToEastern(k.Timestamp)` (a `DateTimeKind` switch plus `TimeZoneInfo.ConvertTimeFromUtc` against `America/New_York`) once per bar. 60,540 calls: **best 8.35 ms, avg 9.42 ms** over 200 repeats.
- **Body replica.** The whole `EtWindow` loop replicated statement-for-statement -- `Trades.Advance(i)`, the kline reads, the ET conversion, the `inTrade` state machine, `Trades.Enter/Exit` against a fresh `TradeRecorder` per repeat, the end-of-data close -- as plain C#: **avg 11.03 ms/version** over 200 repeats. Extrapolated to 500 versions: **5,515 ms** of thread time.

### Verdict: API-overhead-bound (warm), compile-bound (cold) -- not strategy-internal

The warm critical path is invoke-bound, and the invoke cost is mostly Roslyn scripting-API overhead rather than the strategy's own computation. Against the three hypotheses:

1. **Compile dominates -- false on the warm path, true on the cold path.** Warm: compile is 0 ms of 21,209 ms because 500 of 500 versions hit the cache. Cold: compile is 126,284 of 151,663 ms (83.3%) -- but that is not "the cache is missing", it is the cache being *stampeded*: with `SerializeBacktests = false`, 20 workers start the backtest phase at the same instant, all 20 call `GetOrCreateRunner` for the same path before any of them publishes an entry, and all 20 compile the same script. The miss count is 20, and the 20 compiles cost 126,284 ms (6,314 ms each under 20-way load). The single-entry-per-path cache is correct; it is not thread-safe *against its own miss path*.
2. **Execute dominates and is Roslyn-API overhead -- true, this is the finding.** Warm invoke is 19,796 of 21,209 ms of backtest thread time (93.3%). The strategy's own work in that invoke is 11.03 ms/version (the body replica, at most -- the replica is the full body, and the trace says it is essentially the ET conversion). Measured invoke is 39.59 ms/version. So **39.59 - 11.03 = 28.56 ms per version, 72.1% of every invoke, is Roslyn scripting-API plumbing**. Across 500 versions that is 14,281 ms of the 21,209 ms backtest thread span, i.e. 14,281 / 21,209 = 67.3% of the entire warm backtest phase, and 14,281 / 65,100 = 21.9% of the whole run's 20-core ceiling (20 x 3,255 ms). This is the globals binding / entry-point delegate / call-frame path that `ScriptRunner<T>.Invoke` imposes on every call, and it is paid regardless of how little the script does.
3. **Execute dominates and is strategy-internal -- false.** The strategy's entire per-version computation, replicated exactly as plain C#, is 11.03 ms of a 39.59 ms invoke. `TradeRecorder.Enter`/`Exit` are O(1) state flips plus a `List.Add` on close, `Advances` is a bounds check and a field store, and `EtWindow` never calls `Engine.GetMetric(...)` at all, so `MetricRegistry` and `IMetric.Calculate` are not on this path. Globals setup -- the hypothesis that `RunnerScriptGlobals` and `TradeRecorder` construction costs something -- is **1 ms across 500 versions**. There is nothing left to optimize inside `TradeRecorder` or `MetricRegistry` for this workload.

What the split looks like end to end on the warm pass: of the 21,209 ms backtest thread span, 19,796 ms is invoke (14,281 of it Roslyn plumbing, 5,515 the strategy), 1,195 ms is stat + source read + cache lookup, 0 ms is compile, and 217 ms is unwrapped residual. The entire non-invoke, non-script overhead is **1,195 / 21,209 = 5.6%** -- which is what 29A's 38 ms "derived residual" was actually measuring, and why it could not see the answer.

### Note: what this measurement cannot see

- **`ScriptRunner`'s own internal caching is invisible here.** `CSharpScript.Create` + `CreateDelegate` do internal work (syntax tree, compilation, emitted assembly) that `StrategyManager` does not observe. The compile number is therefore the cost of the whole call, not a breakdown of it.
- **Only one strategy shape was measured.** `EtWindow` is a single `for` loop over 60,540 bars that never touches `MetricRegistry`. A strategy that calls `Engine.GetMetric(...).Calculate(Data)` would move work into the invoke span where this instrumentation can see the total but not the split. The "72% Roslyn" figure is a property of the scripting API's per-call cost, not of one strategy, but the *strategy-internal* share would grow for a metric-heavy script.
- **Absolute per-version numbers do not reproduce 29A's.** The 29A reference run's backtest phase (3,583 ms wall, 71,197 ms thread span over 500 versions) implies ~142 ms/version of thread span; this reproduction measures 39.59-47.07 ms/version of invoke. The two runs were not taken under identical conditions (the 29A pass ran inside the full Avalonia process through the Runner's Optimize path; this one ran headless through a temporary CLI replay hook), and no attempt was made to reconcile them. **The split and the verdict are unaffected** -- they are internal to a single run on the same machine, workload and topology -- but 29A's absolute per-version figure should not be reused as a baseline without re-measuring. This is recorded as an open question, not explained away.
- **The cold-pass stampede is a finding, not a measurement artefact.** 20 misses for 1 distinct script is a real concurrency defect in the cache's miss path; it just was not the question this phase was asked.

### Implication for Phase 31

- **The lever is the invoke, specifically the Roslyn scripting API's per-call cost.** Removing it entirely is worth up to 14,281 / 65,100 = 21.9% of warm wall time -- and the MC phases (826 + 1,308 = 2,134 ms of 3,255 ms wall) would become the critical path before that ceiling is reached, so the practical target is smaller than the theoretical one.
- **The cheap orthogonal win is the compile stampede.** Collapsing 20 concurrent compiles of one script to one removes ~126 s of thread time and ~6 s of wall from a cold Optimize -- a 9,403 ms cold run would approach its 3,255 ms warm shape. That is a `StrategyManager` caching change, deliberately not made here.
- **Do not tune `TradeRecorder` or `MetricRegistry` for this.** They are 11 ms of a 39.59 ms invoke on this workload. 29A's advice stands -- the lever is the cost of one script execution -- and this phase says where in that execution the cost is.

### Instrumentation shipped

- Six stages timed per version, each with one `Stopwatch.GetTimestamp()` pair: cache lookup (key + `TryGetValue`), stat (`File.Exists` + `FileInfo`), source read, compile (`CSharpScript.Create` + `CreateDelegate`, miss path only), globals setup (`TradeRecorder` + `RunnerScriptGlobals`), invoke (`runner(globals)`). Accumulated with `Interlocked.Add` into `long` tick fields on `StrategyManager` -- one atomic add per stage per version, no allocation and no per-iteration logging. Hit/miss/compile counters are `Interlocked.Increment`ed and `Interlocked.Read` back.
- One `[30]` block of three `LogManager.Info(LogTag.Runner, ...)` lines at the end of the run, emitted from the same `finally` as the `[29A]` block, so a cancelled run still logs what accumulated. Line 1 carries every stage in ms; line 2 carries the split as percentages of the backtest phase span plus the unwrapped residual; line 3 carries hit/miss counts, real compiles, invoke ms/version and the backtest phase's share of total wall. No new `LogTag` and no new package.
- The `[29A]` block was left exactly as it was, including its derived-residual line, so the two phases' numbers are directly comparable in one log.
- `OptimizationRunner.ResetStageTimers` also calls `StrategyManager.ResetInstrumentation()`, because the stage accumulators live on a DI singleton and a second Optimize pass would otherwise inherit the first pass's numbers. The UI runs one Optimize pass at a time behind `IsBusy`.
- Temporary scaffolding, like 29A's: it ships on, with no enable/disable flag, and a future phase removes it or downgrades it to Debug.
- The Phase 30 workload was driven through a temporary `--replay-optimize-30` CLI hook in `Program.cs` that replicated the Runner's Optimize path headlessly (same grid construction from `StrategyParameterParser` + `OptimizerGridRanges`, same `OptimizationRunConfig`, same data load). It also carried the two attribution probes. The hook was removed before commit: `Program.cs` is byte-identical to its previous revision, and the commit touches no file the hook needed.

## Phase 31: Thread-Safe Single-Flight Compile [COMPLETED]

Ship date: 2026-10-10. Fixes the one defect Phase 30 found but did not own.

Phase 30 measured the cold Optimize path and found the compile cache correct but *stampeded*: on a 500-combination cold pass, 20 backtest workers started at the same instant, all 20 missed the cache for the same path before any of them published an entry, and all 20 compiled the same script. Twenty real compiles, 126,284 ms of compile thread time, 83.3% of the cold backtest thread span -- for one distinct script. The cache was right; its miss path was not synchronized. `GetOrCreateRunner` computed the key, read the dictionary, and constructed the runner with nothing between the read and the write, so `ConcurrentDictionary` bought concurrency on the hit path and nothing at all on the miss path.

This phase makes that path single-flight per cache key. Nothing else changed: same key (the path, validated against last-write time and size), same cache instance, same eviction (none), same size cap (none), same compile-failure semantics, same script execution path, same instrumentation shape, same `OptimizationRunner`.

### Mechanism

- A second `ConcurrentDictionary<string, SemaphoreSlim>` -- `_compileGates`, one gate per cache key -- alongside the existing `_cache`. This shape rather than `ConcurrentDictionary<string, Lazy<ScriptRunner<object?>>>` because `Lazy` (with either thread-safety mode) caches a thrown exception for the lifetime of the entry, which would have changed compile-failure semantics: today a failed compile publishes nothing, so the next caller retries. The semaphore shape keeps the publish point exactly where it was -- after `CreateDelegate` returns -- so a failed compile still caches nothing, still retries, and leaves no gate held. No `Lazy<` existed anywhere in the codebase; `SemaphoreSlim` already did (`StrategyManager._throttle`, `Mt5TickServer._writeLock`), so this also mirrors the established pattern instead of introducing one.
- `GetOrCreateRunner` became `GetOrCreateRunnerAsync`. The fast path is the same work as before, statement for statement: one `TryGetValue` plus the `LastWriteUtc` / `Size` validation, and a hit returns the cached `ScriptRunner<object?>` without touching a gate. Only a miss reaches the gate, which is why the warm path is unchanged by construction and not just by measurement.
- The gate is awaited (`await gate.WaitAsync(ct)`), not blocked on, so a caller that lost the race yields its thread instead of parking one of the 20 backtest workers for the duration of another caller's compile. A cancellation observed before the gate is acquired leaves nothing held; `finally` releases the gate and then removes it from the map.
- Inside the gate the cache is re-checked against the caller's own (path, last write, size). The winner compiles and publishes; every caller that arrives while that compile runs finds the entry on the double-check, logs `compile cache hit (single-flight)`, and returns the same delegate instance rather than compiling again.
- The gates are per key, so distinct keys never contend: there is no global lock and no `lock (dict)`, and unrelated scripts still compile in parallel. The worst case for two distinct keys is the same as before the change.
- The gate entry is dropped in `finally` once its compile completes, so the map holds "keys currently compiling", not "keys ever compiled". The publish happens-before the release, so a caller that took this gate before the removal and a caller that takes a fresh gate after it both re-check the cache and observe the entry; the removal itself cannot add a compile. On the exception path nothing was published and the gate is dropped anyway, which is what lets a retry proceed immediately.
- Hits and misses are still classified by the *first* lookup, before the gate, so `hits + misses == versions` stays true (480/20 cold, 500/0 warm) and the cold row of Phase 30's table remains directly comparable. The double-check hit does not increment `CacheHits`; it is visible in the Debug log instead.

### Numbers

Workload is Phase 30's, unchanged: 500 combinations (`OptimizerMaxCombinations` 500, sampled with seed 42 from an `EtWindow` grid of 3,362 = Side 2 x StartMinute 41 x EndMinute 41), `OptimizerCoarseIterations` 100, `OptimizerRefineIterations` 1000, `OptimizerRefineTopN` 20, `GOLD.pro@30m` (60,540 bars), 20 cores. Two passes in one process: pass 1 cold (first Roslyn use in the process), pass 2 warm.

Before and after were measured on the same machine in the same session through the same temporary `--replay-optimize-31` CLI hook. The "before" column is `33e7cee` with only the fix reverted (`git checkout 33e7cee -- src/core/StrategyManager.cs`) -- not the Phase 30 log -- so the two columns differ only by the change under test. That pre-fix re-measure reproduced Phase 30: 20 compiles and 126,017 ms of compile thread time, against the 126,284 ms / 83.3% the Phase 30 archive records for the same stage and the 126,985 ms / 82.4% this phase's brief quotes. All three are the same 20 concurrent compiles sampled on different passes.

| Metric, cold pass | Before (`33e7cee`) | After | Delta |
|---|---|---|---|
| Real compiles | 20 | 1 | -19 |
| Compile thread time | 126,017 ms | 5,377 ms | **-120,640 ms (-95.7%)** |
| Compile share of backtest thread span | 83.4% | 4.0% | -79.4 pp |
| Cache hits / misses | 480 / 20 | 480 / 20 | unchanged |
| Backtest phase span, thread-summed | 151,115 ms | 133,442 ms | -17,673 ms |
| of which invoke | 23,376 ms (46.75 ms/version) | 24,065 ms (48.13 ms/version) | noise |
| of which unwrapped residual (now the single-flight wait) | 589 ms | 102,781 ms | +102,192 ms |
| Total cold wall | 9,500 ms | 8,526 ms | **-974 ms (-10.3%)** |

| Metric, warm pass | Before | After | Delta |
|---|---|---|---|
| Cache hits / misses | 500 / 0 | 500 / 0 | unchanged |
| Real compiles | 0 | 0 | unchanged |
| Compile thread time | 0 ms | 0 ms | unchanged |
| Invoke | 24,442 ms (48.88 ms/version) | 22,986 ms (45.97 ms/version) | noise |
| Total warm wall | 3,540 ms | 3,195 ms | noise |

Warm per-version invoke is unchanged within run-to-run noise: 48.88 ms/version before, 45.97 and 46.80 ms/version in two post-fix cold+warm runs, against Phase 30's 39.59 ms/version archive figure and the 46.79 ms/version this phase's acceptance criteria cite. The spread across runs is larger than the difference between before and after -- which is the claim: the warm path never acquires a gate.

The 19 eliminated compiles did not vanish from the backtest span, they became *waits*: 19 workers x ~5,410 ms = 102,781 ms, which is exactly the post-fix "unwrapped residual" in the [30] block ([29A]'s derived residual, version span minus script duration). That is the honest reading of the thread-summed span: it measures span, not work. The work eliminated is the 120,640 ms of compile thread time; the waiters still hold their version spans while they wait.

### Log evidence

Every count below comes from grepping the post-fix cold log; the pre-fix log was grepped the same way for comparison.

- `Select-String 'single-flight compile'` -> **1 line** (one real compile). Pre-fix, the equivalent grep for the compile line returned 20 lines.
- `Select-String 'compile cache miss'` -> 20 lines, the 20 first-wave lookups that found no entry. Classification unchanged from Phase 30.
- `Select-String 'compile cache hit \(single-flight\)'` -> 19 lines, the workers that arrived during the compile and found the published entry.
- `Select-String 'compile cache hit: '` -> 980 lines across both passes (480 cold + 500 warm), unchanged from before.

The compile-start line is `LogManager.Debug(LogTag.Strategy, "single-flight compile: ...")`, emitted only once the gate has been acquired and the double-check has missed. The pre-existing per-miss line was reworded from `compile:` to `compile cache miss:` so that a grep for the compile itself is not confused with the 20 first-wave misses. No Info-level line was added, no `LogTag` was added, and the hit line is unchanged.

### Verification

- **Determinism.** All 500 versions of both post-fix passes were compared element-by-element against the archived 29A batch `batch_20261010-161522.json`: parameter name/value tuples in grid order, trade counts, and `NetPnl` as exact doubles. **Cold mismatches 0, warm mismatches 0.** The pre-fix re-measure also produced 0 mismatches, which is what makes the before/after comparison a comparison of the same workload.
- **Same-key single-flight.** The cold 500-combination pass: 20 first-look misses, 1 compile, 19 single-flight hits, 480 later hits.
- **Distinct keys still compile in parallel.** Probe on two copies of the strategy at two distinct paths (cache key is the path): two cold compiles run sequentially took **718 ms**; eight concurrent backtests spread over the same two keys took **342 ms**, with 2 compiles, 8 misses and 6 single-flight hits. A global lock would have left the concurrent case near the sequential wall (~718 ms), and no gate at all would have produced 8 compiles. This is the check that the fix is per key and not a global serialization.
- **Warm path.** 500/500 hits, 0 compiles, per-version invoke within noise (table above).
- **No poisoned state on failure.** Nothing is published until `CreateDelegate` returns, so a throwing compile leaves no cache entry, no cached exception, and no gate held.

### Notes: what this does not change, and what it cannot see

- **The stampede cost thread time and machine load, not wall.** Cold wall fell 9,500 -> 8,526 ms (-10.3%) while compile thread time fell 95.7%. Twenty compiles of one script overlap almost perfectly across 20 cores, so the cold backtest phase was already bounded by roughly one compile's duration; single-flight removed the other 19 compiles' CPU demand and let the surviving compile finish sooner (5,377 ms uncontended, against ~6,300 ms each under 20-way load). Phase 30's implication -- "removes ~126 s of thread time and ~6 s of wall" -- predicted the thread time exactly and the wall time optimistically. The durable win is the machine load: a cold Optimize no longer saturates all 20 cores compiling one script.
- **One cold compile is mostly Roslyn first-use, not marginal compile cost.** Once the process had compiled a script, the probe compiled the same source at a fresh path in roughly 350 ms per key (718 ms for two sequential distinct-key compiles). The 5,377 ms cold figure is therefore the whole first compile in a process -- assembly loading, compilation machinery, emission -- not what a second, different strategy would cost. This is the same limit Phase 30 recorded: `ScriptRunner`'s internal work is invisible to `StrategyManager`.
- **Instrumentation meaning.** The [30] stage timers keep their meaning and the cold hit/miss row stays comparable to Phase 30's; the number that changed is "Real compiles" (20 -> 1). The [29A] derived residual, however, is no longer a per-version overhead proxy on a cold pass, because it now absorbs the single-flight wait (102,781 ms of it). The [30] block is the honest number for that run.
- **Untouched, deliberately.** No change to the cache key, eviction, size cap, or storage type beyond what the gate requires; no second cache; no change to `OptimizationRunner` (its `Parallel.ForEachAsync` with `MaxDegreeOfParallelism = backtestOuter = min(combinations, cores) = 20` is the contention shape this fix had to cover, and it was not modified); no new package; no new `LogTag`; no UI change; no change to compile-failure semantics.
- **What this does not fix.** The warm invoke cost Phase 30 identified -- 72% of every invoke is Roslyn scripting-API plumbing -- is untouched. This phase shortens nothing on a warm run; it removes a cold-run defect that Phase 30 measured but deliberately did not own.

### Rollback path

Revert this commit. The change is one source file (`src/core/StrategyManager.cs`) plus the two roadmap docs, with no schema, persisted state, config key, or cache format involved and no migration in either direction. `git checkout 33e7cee -- src/core/StrategyManager.cs` restores the unsynchronized miss path and with it the 20-compile cold stampede; the cache, the key, and the script execution path are otherwise identical. The determinism check (`--replay-optimize-31`, temporary) would need to be re-added to re-confirm the baseline numbers after a revert, exactly as it was to produce them here.

### Instrumentation shipped

- No new instrumentation. The phase adds one Debug line and rewords one existing Debug line; the Phase 30 stage accumulators, the `[30]` summary block, and the `[29A]` block are all unchanged, so a Phase 31 log is directly comparable to a Phase 30 one.
- The workload was driven through a temporary `--replay-optimize-31` CLI hook in `Program.cs` that replicated the Runner's Optimize path headlessly (same grid construction from `StrategyParameterParser` + the persisted `OptimizerGridRanges`, same `OptimizationRunConfig`, same cached data load), ran the cold and warm passes in one process, printed the `StrategyManager` snapshot after each pass, compared both passes against the archived batch, and carried the distinct-key parallelism probe. The hook was removed before commit: `Program.cs` is byte-identical to its `33e7cee` revision and the commit touches no file the hook needed.
