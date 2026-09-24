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