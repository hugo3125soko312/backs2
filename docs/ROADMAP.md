# Roadmap: backs2 Trading Engine

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
- [ ] Display run results (Sharpe, WinRate) -- **deferred to Phase 11A** (`BacktestResult` model). Properties remain on `StratsViewModel` as placeholders.

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

## Phase 10.7: Chart Tab [UNBLOCKED]

Status: **UNBLOCKED -- ScottPlot replaced with OxyPlot.Avalonia 2.1.0-Avalonia11.**

### Resolution
The ScottPlot.Avalonia white-background regression was resolved by switching the charting stack. `OxyPlot.Avalonia 2.1.0-Avalonia11` renders the dark background correctly because `PlotModel.Background` and `PlotModel.PlotAreaBackground` are first-class properties applied during rendering, unlike the ScottPlot canvas-clear action that ignored them.

### Package Change
- Removed: `ScottPlot.Avalonia 5.0.55`.
- Added: `OxyPlot.Avalonia 2.1.0-Avalonia11`.
- Theme include: `<StyleInclude Source="avares://OxyPlot.Avalonia/Themes/Default.axaml"/>`.

### What Has Been Resolved
- [x] Dark chart background (`#1E1E1E`) renders correctly on both `PlotModel.Background` and `PlotAreaBackground`.
- [x] Candlestick rendering via `CandleStickSeries`.
- [x] Right-side price axis (`AxisPosition.Right`).
- [x] Gridlines removed (`LineStyle.None` on both axes).
- [x] Redundant year labels eliminated (`StringFormat = "yyyy"`, `IntervalType = DateTimeIntervalType.Years`).
- [x] Custom tracker popup -- `#2A2A2A` background, white text, `#888888` crosshair lines via `DefaultTrackerTemplate`.
- [x] Context menu with scale options: Auto, Logarithmic, Percent, Indexed to 100, Invert scale.
- [x] Percent and Indexed-to-100 transforms applied to OHLC items at construction time.

### Phase 10.7A -- Charting Foundation [COMPLETED]
- [x] Charting library selected: OxyPlot.Avalonia 2.1.0-Avalonia11.
- [x] `ChartViewModel`: `[ObservableProperty] List<Kline> Series`, `[ObservableProperty] string SelectedSymbol`, `[ObservableProperty] string SelectedTimeframe`, `[ObservableProperty] PlotModel PlotModel`.
- [x] Symbol picker: mirrors the Strategies-tab pattern (ComboBox of cached symbols + free-text + refresh).
- [x] Data flow: `ChartViewModel` calls `DataManager.GetSymbolDataAsync(symbol, timeframe)` and rebuilds `PlotModel` on `OnSeriesChanged`.
- [x] Candlestick rendering: open/high/low/close per bar via `CandleStickSeries`.

### Phase 10.7B -- Volume [COMPLETED]
- [x] Volume subplot added below the price panel (price 0.22-1.00, volume 0.00-0.18 in `PlotModel` panel fractions).
- [x] Volume bars colored by candle direction: green (`#26A69A`) when `Close >= Open`, red (`#EF5350`) otherwise.
- [x] Shared X-axis between price and volume panels via a single bottom `DateTimeAxis`.
- [x] TradingView-style abbreviated volume labels on the right (`1.2K` / `1.2M` / `1.2B` / `1.2T`) via `LabelFormatter`.
- [x] **Known workaround:** `RectangleBarSeries.UpdateData()` throws `NotImplementedException` in OxyPlot.Avalonia 2.1.0 when the series is reset from populated to empty. Replaced with two `LinearBarSeries` (one per color) to avoid this code path.
- [ ] **Known issue (Phase 10.7E):** Candlestick bodies render with jagged/aliased edges. Suspected DPI-related but reproduces on multiple monitors and does not resolve after monitor switch, so it is not a DPI problem per se.

### Phase 10.7C -- Timeframe Switching [PENDING]
- [ ] Timeframe picker: `1m`, `5m`, `15m`, `1h`, `4h`, `1D`, `1W`.
- [ ] **Data source decision:** Massive `/v2/aggs/ticker/{ticker}/range/{multiplier}/{timespan}/{from}/{to}` supports intraday natively. Yahoo supports `interval=1m|5m|15m|1h|1d|1wk`. Decide whether to fetch per-timeframe from provider, or resample locally from the cached 1D series.
  - Recommendation: **fetch per-timeframe from the provider**, cache each timeframe under a distinct symbol key (e.g. `XAUUSD@1h`), so `DataManager` handles it as just another series.
- [ ] Progressbar during timeframe switch: `[ObservableProperty] bool IsLoading`, `[ObservableProperty] double LoadingProgress`. Bind an indeterminate or determinate `ProgressBar` that is visible only while switching. `DataManager`'s `IProgress<string>` gives us a signal we can turn into ticks.

### Phase 10.7D -- Crosshair & Readout [PENDING]
- [ ] Mouse crosshair tracking on the price panel.
- [ ] Readout box showing O/H/L/C/Volume/timestamp for the hovered bar.
- [ ] Optional: snap crosshair to nearest bar.
- [ ] **Lock price to bar ratio** (deferred from 10.7A) -- enforce a fixed pixels-per-price-unit ratio during pan/zoom via custom pointer-event handling on `PlotController`.

### Phase 10.7E -- Polish [PENDING]
- [ ] Save/restore last-viewed symbol + timeframe between app runs (write to `config/settings.json` via `ConfigManager`).
- [ ] Empty state: if no symbol selected or no data, show a friendly message instead of a blank canvas.
- [ ] Error state: if provider fetch fails, show the fallback chain's progress messages in a status line.
- [ ] **Selectable error texts.** Status line and error messages must use `SelectableTextBlock` instead of `TextBox` or `TextBlock`, so users can select and copy exception text.
- [ ] **Data tab empty state bug.** DataGrid shows "2 of 2" in the infobar but no rows render. Investigate: (a) `DataGrid` inside a vertical `StackPanel` does not size correctly and renders zero rows; (b) missing `PropertyChanged` notification when the whole collection is replaced; (c) `DataContext` not assigned on the UserControl. Confirm root cause and fix.
- [ ] **Candlestick body rendering is not sharp.** Candle bodies and wicks render with jagged/aliased edges against the dark background. Tested on two monitors with different DPI: the issue persists on both and does not resolve after moving the window between them, so it is not a standard per-monitor DPI scaling problem. Candidate causes to investigate:
  - `PlotModel` renderer anti-aliasing settings (`PlotModel.Renderer` / `PlotView` render options).
  - `CandleStickSeries.StrokeThickness` too thin (1.0) against a dark background at typical candle widths.
  - Skia/Avalonia bitmap scaling: the `PlotView` may render at logical pixels and be scaled up by the compositor. Check `RenderOptions.BitmapInterpolationMode` on the `PlotView` and whether the render surface matches the physical pixel size.
  - Integer alignment: candle X positions derived from `DateTimeAxis.ToDouble` do not snap to integer pixel boundaries, causing sub-pixel rendering that manifests as a soft/aliased edge.
  - OxyPlot.Avalonia renderer wiring: the 2.1.0-Avalonia11 package may not use the SkiaSharp renderer by default; confirm which renderer is active and whether a `SkiaRenderContext` with proper anti-aliasing is in use.

### Dependencies
- Depends on Phase 10 (DI wiring) -- done.
- Depends on Phase 10.5 (cache performance) -- done; chart fetches hit the same tiers.
- Feeds into Phase 11 (Monte Carlo) -- the Analytics tab will want to plot equity curves using the same OxyPlot plumbing.

---

## Phase 10.8: Metrics Library Expansion [PLANNED]

Scope: grow `MetricRegistry` from its current 3 metrics (SessionHigh, VWAP, ZScore) into a broad library. Classify every metric by **lag** (leading / lagging / mixed) and **usage** (trend / momentum / volatility / volume / breadth). UI exposes filtering by these categories.

### Phase 10.8A -- Metric Taxonomy
- [ ] Extend `IMetric` with metadata:

    public enum MetricLag { Leading, Lagging, Mixed }
    public enum MetricCategory { Trend, Momentum, Volatility, Volume, Support, Breadth, Statistical }

    public interface IMetric
    {
        string Name { get; }
        MetricLag Lag { get; }
        MetricCategory Category { get; }
        string Description { get; }
        double Calculate(List<Kline> data);
    }

## Phase 11: Monte Carlo & Performance Analytics [PLANNED]

Target: reproduce the TradingView / Fractalyst-style statistics panel -- In-Sample Analysis, Monte Carlo Analysis, Out-of-Sample Analysis, plus full Performance / Trade / Risk metrics.

### Phase 11A -- Backtest Result Model
- [ ] `BacktestResult` record: equity curve, trade list, per-trade PnL, holding durations, win/loss counts.
- [ ] `Trade` record: entry time, exit time, entry price, exit price, direction, size, PnL, PnL%, MAE, MFE.
- [ ] `StrategyManager.RunBacktestFromFileAsync` returns a populated `BacktestResult` (via `ScriptGlobals.Result`). Scripts declare a strategy type or register trades explicitly.

### Phase 11B -- In-Sample Metrics
- [ ] CAGR (Compound Annual Growth Rate).
- [ ] Expectancy (average PnL per trade in currency and %).
- [ ] Max Drawdown (absolute and %).
- [ ] Sortino ratio.
- [ ] Net Profit, Profit Factor, Payoff ratio.
- [ ] Sample Size (trade count).
- [ ] Avg Duration, Max Consecutive Wins (Max CW), Max Consecutive Losses (Max CL).
- [ ] Win rate, loss rate, breakeven rate.
- [ ] Frequency (trades per week/month), Exposure (% time in market).
- [ ] Sharpe ratio, Sortino ratio.
- [ ] MAR, RoR, R2, CVaR, p-value.
- [ ] Alpha, Beta (vs Buy & Hold).
- [ ] Compounding, Avg Win, Avg Trade, Avg Loss.
- [ ] Rolling Expectancy, Avg Win Duration (Avg W Dur), Avg Loss Duration (Avg L Dur).
- [ ] Max Equity (Max Eq), Min Equity (Min Eq), Rolling Sortino.
- [ ] Current Drawdown (Curr DD), DAR, Kurtosis, Skewness, VaR, Ulcer index.

### Phase 11C -- Monte Carlo Simulation
- [ ] Bootstrap resampling of trade sequence (with and without replacement).
- [ ] Randomized trade ordering (shuffle).
- [ ] Optional: randomized entry/exit jitter.
- [ ] Configurable iteration count (default 1000).
- [ ] Output distribution per metric (CAGR, Expectancy, Max DD, Sortino).
- [ ] Report: Best / Median / Worst / Mean / StdDev per metric.
- [ ] Confidence intervals (5% / 95%).
- [ ] Histogram rendering (optional phase 11E).

### Phase 11D -- Out-of-Sample Analysis
- [ ] Split the data: In-Sample vs Out-of-Sample windows (ratio configurable, default 70/30).
- [ ] Run identical metrics on both windows.
- [ ] Report side-by-side comparison.
- [ ] Walk-forward analysis (rolling window) -- optional sub-phase.

### Phase 11E -- GUI
- [ ] New **Analytics** tab (or a pane inside Strategies).
- [ ] Table matching the TradingView layout: three-column header (In-Sample / Monte Carlo / Out-of-Sample).
- [ ] Row labels from Phase 11B/11C.
- [ ] Color-coded cells (green positive, red negative) matching the reference.
- [ ] Export results to CSV / JSON for external review.

### Phase 11F -- Strategy Authoring Contract
- [ ] Define how a strategy declares trades. Options:
  - **A)** Strategy returns a `List<Trade>` explicitly.
  - **B)** `ScriptGlobals` exposes `Enter(side, price)`, `Exit(price)`, `Size`, etc. Engine tracks positions.
  - **C)** Strategy returns a signal series (long/short/flat per bar); engine derives trades.
- [ ] Decision needed before Phase 11A is implemented.

### Dependencies
- Depends on Phase 8 (symbol selection) -- Monte Carlo runs require a chosen instrument and date range.
- Depends on Phase 10 (ChartView wiring) if histograms/equity curves are to be plotted.
- Package candidates: `MathNet.Numerics` (statistics), `OxyPlot.Avalonia` (already present, for histograms/equity).

## Phase 12: Hardening & Housekeeping [PLANNED]