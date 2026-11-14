<!-- docs\ARCHITECTURE.md -->
# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: Microsoft.Extensions.DependencyInjection, managed in App.xaml.cs.
- Service Lifecycle:
   - Singletons: LogManager, ConfigManager, DataManager, StrategyManager, StrategyCatalogService, PresetService, MetricRegistry, CacheManager, Mt5TickServer, Mt5TickService, ComputeWorkerManager.
   - Transients/Keyed: IDataProvider implementations (resolved via GetRequiredKeyedService).
- Enforcement: No static global variables. All dependencies resolved via constructor injection.

## 2. Data Pipeline (3-Tier)
1. Memory Tier (IMemoryCache): RAM-based fast lookup for immediate data access.
2. Persistence Tier (CacheManager): Async JSON storage at data/cache/. Compact disk format via KlineCompact + source-generated KlineJsonContext.
3. Network Tier (IDataProvider): Abstracted interface with automatic resilience.
   - Resilience: All providers use a named "ResilientClient" (configured via AddStandardResilienceHandler).
- Flow: DataManager checks Memory -> Disk -> Network.
- Cache key: symbol@timeframe (e.g. XAUUSD@1D, EURUSD@1h).
- Provider Fallback: The network tier walks AppSettings.ProviderOrder. First provider with a non-empty result wins. Failures and empty results fall through; errors never abort the chain.
- Preferred provider: `DataManager.GetSymbolDataAsync(symbol, timeframe, progress, preferredProvider)` accepts an optional provider key. When set, cache hits whose `source` differs are skipped, and the network walk tries `preferredProvider` first before falling through to the rest of `ProviderOrder`. When null (the `Auto` case), behavior is identical to the standard walk. The memory cache value is `MemoryEntry(Bars, Source)` so the origin travels with the cached bars.
- Timeframe-aware: DataManager.GetSymbolDataAsync(symbol, timeframe, progress); a one-arg overload defaults to "1D".
- Progress reporting: DataManager.GetSymbolDataAsync(symbol, timeframe, IProgress<DataProgress>) reports DataProgress(Message, IsDeterminate, Percent). RAM/disk hits report IsDeterminate=false (instant). The network tier reports IsDeterminate=true with Percent = attemptIndex / totalProviders * 100. A legacy IProgress<string> overload adapts to the new contract for consumers that only need text.

## 3. Metrics Engine
- IMetric Interface: Contract for all mathematical indicators.
   - Metadata: Name, Lag (Leading / Lagging / Mixed), Category (Trend / Momentum / Volatility / Volume / Support / Breadth / Statistical), Description.
   - Calculation: double Calculate(List<Kline> data).
   - Invariant: every IMetric must return a defined value for degenerate inputs (empty list, flat series, single bar) and must never throw. Metrics are pure and stateless — same input yields same output, always.
- MetricRegistry: Centralized singleton. Registers every metric at construction.
   - GetMetric(string) — never throws, never returns null. Returns NullMetric.Instance (Calculate => 0) on a miss, so strategy scripts calling Engine.GetMetric("X").Calculate(Data) never NRE or abort a run.
   - TryGetMetric(string, out IMetric) — for callers that need to distinguish a hit from a miss.
   - GetAvailableMetrics() — legacy name-only view.
   - Metrics — IReadOnlyCollection<IMetric> exposing full metadata for the Metrics tab.
- Registered metrics (Phase 10.8A / 10.8B):
   - VWAP (Volume, Lagging) — typical-price volume weighted average.
   - CVD (Volume, Leading) — CLV x Volume delta proxy: Volume * (2*(Close-Low)/(High-Low) - 1), summed across the series.
   - RSI (Momentum, Lagging) — 14-period, Wilder smoothing. Flat series returns 50.
   - ATR (Volatility, Lagging) — 14-period, Wilder smoothing.
   - SessionHigh (Support, Lagging) — highest High across the series.
   - ZScore (Statistical, Mixed) — standard score of the last Close vs the series mean and population standard deviation. Returns 0 when variance is zero.
   - VP-POC / VP-VAH / VP-VAL (Support, Mixed) — Volume Profile Point of Control and 70% Value Area bounds. Each is a pure function of the input series; no shared state, no memoisation. The engine is a tight two-pass routine (min/max/totalVolume sweep, then binning sweep) with no LINQ and no Math.Clamp in the inner bin loop.

## 4. Strategy Execution (Disk-Streamed)
- Source of truth: data/strategies/*.cs on disk. There is no in-app strategy builder.
- Catalog: StrategyCatalogService (singleton) scans data/strategies/ on startup and watches it via FileSystemWatcher (debounced 250 ms). It exposes:
  - Current — read-only snapshot of StrategyDescriptor entries (path, name, last-write UTC, size).
  - Changed — event raised after every catalog refresh.
  - ReadSourceAsync(path) — lazy source load, called only when a strategy is clicked.
  - Refresh() — manual full rescan.
  - Auto-creates data/strategies/ if missing.
- Execution: StrategyManager uses the Roslyn Scripting API. Compiled runners cached by (path, lastWriteUtc, size).
- Concurrency: SemaphoreSlim(1,1) throttles backtests to one at a time.
- Script Shape: Top-level statements running against RunnerScriptGlobals (src/core/Runner/RunnerScriptGlobals.cs):
  - Engine (MetricRegistry) — access metrics via Engine.GetMetric("VWAP").
  - Data (List<Kline>) — the loaded series.
  - Log(string) — delegates to LogManager.Info.
  - Trades (TradeRecorder) — records trades via Enter(side, price) / Exit(price). The engine advances the cursor via Advance(index) so entry/exit timestamps come from the Kline at the current index.
  - Params (Dictionary<string, string>) — the current Runner parameter values, keyed by declared name.
- Strategy Parameters: `// @param <Name> <Type> = <Default> [options: "A", "B"]` lines at the top of the file. Parsed by StrategyParameterParser; not compiled, not executed. Supported types: int, double, bool, string.
- Build rule: backs2.csproj contains a Compile Remove for data\**\*.cs. Strategies are Roslyn-evaluated at runtime and must never be compiled into the assembly.

## 5. Configuration & Auto-Generating UI
- Settings File: ConfigManager handles config/settings.json — non-secret engine state (threading, resilience, provider order).
- ConfigDirectory: ConfigManager exposes `public string ConfigDirectory => "config";` as the single source of truth for non-secret on-disk state. PresetService uses it to resolve `config/presets/`.
- Secrets: API keys are read from .env at startup and exposed as read-only ConfigManager properties (TwelveDataApiKey, TiingoApiKey, MassiveApiKey, YahooApiKey, FinnhubApiKey, FcsApiKey). Secrets are never persisted to settings.json. Placeholder values (`key`, `placeholder`, `todo`, `xxx`) are normalized to empty by `ConfigManager.NormalizeValue` so an unregistered provider short-circuits without issuing a request.
- Dynamic Reflection Engine:
  - ConfigViewModel reflects all public instance properties on AppSettings (excluding ProviderOrder) into SettingItem wrappers.
  - Controls are rendered dynamically in ConfigView.xaml based on data type:
    - int / double -> NumericUpDown (steppers with appropriate increments)
    - bool -> CheckBox
    - string containing "Color" -> ColorPicker (custom ColorPickerWindow.xaml)
    - string -> TextBox
  - Adding a new property to AppSettings automatically generates its UI control without modifying XAML or ViewModel code.
- Dynamic Provider Discovery:
  - ConfigViewModel uses reflection over IDataProvider to automatically discover all non-abstract provider classes in the assembly.
  - Any new provider implementing IDataProvider is automatically listed in the "Available Providers" listbox for ordering.
- Provider Order: AppSettings.ProviderOrder is an ordered list of provider keys (default: ["Twelve Data", "Tiingo", "Yahoo", "Massive", "Binance", "Finnhub", "FCS API"]). Edited via the Config tab. Consumed by DataManager.
- Timeframes: Canonical list lives in TimeframeMap.All: 1m, 2m, 3m, 5m, 15m, 30m, 45m, 1h, 2h, 3h, 4h, 1D, 1W.

## 6. Providers (IDataProvider)
- Contract:
  - Name (string)
  - Task<List<Kline>> FetchAsync(string symbol)
  - Task<List<Kline>> FetchAsync(string symbol, string timeframe)
- Each provider is registered as a keyed transient in App.xaml.cs.
- Symbol resolution is per-provider:
   - Twelve Data: EURUSD -> EUR/USD, XAUUSD -> XAU/USD, AAPL -> AAPL
   - Tiingo: Symbols pass through directly (e.g. AAPL, EURUSD)
   - Yahoo: XAUUSD -> XAUUSD=X, EURUSD -> EURUSD=X, AAPL -> AAPL
   - Massive: XAUUSD -> C:XAUUSD, EURUSD -> C:EURUSD, AAPL -> AAPL
   - Binance: Symbols pass through directly (e.g. BTCUSDT, ETHUSDT). Crypto spot only.
   - Finnhub: US equities pass through (AAPL); forex resolves to OANDA:{BASE}_{QUOTE} (EURUSD -> OANDA:EUR_USD).
   - FCS API: EURUSD -> EUR/USD, XAUUSD -> XAU/USD. Slash-separated.
- Timeframe resolution is centralized in TimeframeMap:
   - ToTwelveData(tf) -> 1min, 5min, 15min, 30min, 45min, 1h, 2h, 4h, 1day, 1week.
   - ToTiingo(tf) -> 1min, 2min, 3min, 5min, 15min, 30min, 45min, 1hour, 2hour, 3hour, 4hour, daily, weekly.
   - ToMassive(tf) -> (multiplier, timespan).
   - ToYahoo(tf) -> interval string or null.
   - ToBinance(tf) -> 1m, 3m, 5m, 15m, 30m, 1h, 2h, 4h, 1d, 1w.
   - ToFinnhub(tf) -> 1, 5, 15, 30, 60, D, W.
   - ToFcs(tf) -> 1m, 5m, 15m, 30m, 1h, 2h, 4h, 1d, 1w.
   - LookbackDays(tf) — per-timeframe fetch window.
- Twelve Data provider: GET https://api.twelvedata.com/time_series?symbol={symbol}&interval={interval}&outputsize=5000&apikey={key}
- Tiingo provider: GET https://api.tiingo.com/iex/{symbol}/prices (intraday) and GET https://api.tiingo.com/tiingo/daily/{symbol}/prices (EOD).
- Massive provider: GET https://api.massive.com/v2/aggs/ticker/{ticker}/range/{multiplier}/{timespan}/{from}/{to}
- Yahoo provider: GET https://query1.finance.yahoo.com/v8/finance/chart/{symbol}
- Binance provider: GET https://data-api.binance.vision/api/v3/klines?symbol={symbol}&interval={interval}&startTime={ms}&endTime={ms}&limit=1500. Keyless. Paginates in 200-day windows (server cap on startTime/endTime span). Crypto spot only; no volume filter since zero-volume bars are legitimate.
- Finnhub provider: GET https://finnhub.io/api/v1/{stock|forex}/candle?symbol={symbol}&resolution={resolution}&from={unix}&to={unix}&token={key}. Returns empty when status is not "ok" — historical candles are gated behind a paid plan on the free tier.
- FCS API provider: GET https://api-v4.fcsapi.com/forex/history?symbol={symbol}&period={period}&length=500&access_key={key}. Response is a JSON object keyed by Unix seconds, each value `{o, h, l, c, v}`. Free plan uses `length` (500 max); `from`/`to` are corporate-tier only.
- Secrets: providers read keys from ConfigManager read-only properties sourced from .env — never from AppSettings. Placeholder values are normalized to empty by `ConfigManager.NormalizeValue` so unregistered providers short-circuit without issuing a request.

## 7. Caching
- Disk format: KlineCompact record — flat {T, O, H, L, C, V} with T as Unix milliseconds. Serialized via source-generated KlineJsonContext.
- Origin discriminator (2026-10-28): cache envelopes carry DataOrigin (Legacy=0 / RawTick=1 / TimeframeFetch=2). Legacy files load unchanged; IDataOriginResolver.FromSourceStatic resolves them lazily from the source string (MT5 Parsed -> RawTick, else -> TimeframeFetch). New writes stamp origin via CacheManager.SaveCacheAsync(key, data, source, DataOrigin); CacheMetadata.Origin exposes it to the Data tab and IQueueOriginGate.
- Legacy-format detection: LoadCacheAsync returns null on empty files, malformed JSON, or all-zero payloads, so DataManager refetches.
- Metadata parsing: `CacheManager.ReadCacheMetadata` scans the JSON payload for any non-zero volume to expose `HasVolume` for the Data tab without fully deserializing the file into memory. Also collects running `MinT`/`MaxT` in the same pass — does not assume ascending bar order.
- Per-file log lines: "Cache: saved {key} ({n} bars, {ms} ms)" and "Cache: loaded {key} ({n} bars, {ms} ms)".
- Memory-tier observability: DataManager logs "RAM hit" / "RAM miss" for every lookup.
- Benchmark: CacheManager.BenchmarkAsync(int barCount) serializes and loads N synthetic bars, logging elapsed milliseconds.

## 8. GUI (MVVM Toolkit)

- **Shell navigation tree (formerly tab host pattern, Phase 17).** `MainWindow.xaml` hosts a left-docked `TreeView` bound to `MainViewModel.NavigationNodes`, with `SelectedItem` two-way bound to `MainViewModel.SelectedNode`. The content host is a sibling `ContentControl` bound to `MainViewModel.CurrentVm`. Tab content is never declared inline — the tree carries only header labels, and the `ContentControl` template-swaps the View via `App.xaml`'s `DataTemplate` entries. No tab's View or ViewModel is materialized until its node is selected.
- **Tab ViewModel resolution (Phase 17).** `MainViewModel` takes `IServiceProvider` in its constructor and exposes 12 lazily-resolved tab properties (`OverviewVm`, `RunnerVm`, `MonteCarloVm`, `MetricsVm`, `AccountVm`, `DataVm`, `DataAutomationVm`, `Mt5TickVm`, `ProvidersVm`, `ChartVm`, `LogsVm`, `ConfigVm`). Each is `_xxx ??= _sp.GetRequiredService<XxxViewModel>()`, so the DI resolve fires exactly once, on first access. `OnSelectedNodeChanged` invokes the selected node's `Func<object>` factory (which routes to one of these properties) and assigns `CurrentVm`, which raises `PropertyChanged(nameof(CurrentVm))` so the `ContentControl` re-evaluates on tree selection.
- **VM → View mapping (Phase 17).** `App.xaml` declares `<Application.DataTemplates>` with one `<DataTemplate DataType="vm:XxxViewModel"><views:XxxView /></DataTemplate>` per tab. WPF resolves the DataTemplate when `CurrentVm` is set, on tab switch. Views never resolve their own ViewModel and never read `Application.Current.Services`.
- **View code-behind (Phase 17).** `InitializeComponent()` only, plus View-local wiring that does not depend on a specific ViewModel instance (event handlers, `UserInputProcessor.Disable()` calls, tunnelled `KeyDown` handlers). DataContext-dependent wiring runs from `OnDataContextChanged` — e.g. `ChartView` hands its `Plot` to `ChartViewModel.SetPlotControl`, `MonteCarloView` hands the three plots to the corresponding setters, `DataView` subscribes to `OpenQueueRequested`, `DataAutomationView` subscribes to the three calibration events. The old `if (Application.Current is App app) DataContext = app.Services.GetRequiredService<TViewModel>();` pattern is forbidden in View constructors.
- **Startup path (Phase 17).** `Program.Main` → `LogManager.Initialize()` → `new App()` → `app.InitializeComponent()` → `App.OnStartup` (DI container build, `ConfigManager` resolve, `Mt5TickServer.Start()`, `MainViewModel` resolve, `MainWindow` construct, `mainWindow.Show()`). All five stages are instrumented with `Stopwatch` markers logging `[startup] ...` Info lines. Only tab 0's ViewModel (Overview) is constructed at startup — it is `CurrentVm` at first render. Every other tab's ViewModel is constructed on first selection. Measured cold start after Phase 17: ~530 ms (was ~3324 ms).
- Source Generation: All ViewModels use CommunityToolkit.Mvvm attributes ([ObservableProperty], [RelayCommand]).
- XAML Bindings (WPF): `d:DataContext` with `d:DesignInstance` on every View/UserControl root.
- DataGrid use: WPF DataGrid.
- ColorPicker use: WPF custom color dialog (`ColorPickerWindow.xaml`).
- Selectable text policy: TextBlock for clickable list rows; read-only TextBox for source preview, log output, status line.
- RelativeSource binding inside DataTemplates: use `{Binding DataContext.Prop, RelativeSource={RelativeSource AncestorType=UserControl}}`.
- ViewModel partial-class convention: where a tab's ViewModel grows past ~600 lines it is split into partial-class files alongside the core file. Precedent: `Mt5TickViewModel` (core + `.Extraction.cs` + `.Requester.cs` + `.Parsing.cs` + `.Tkc.cs`), `MonteCarloViewModel` (core + `.Plots.cs` + `.Stats.cs` + `.Cluster.cs`), and `RunnerViewModel` (core + `.Run.cs` + `.Optimize.cs` + `.Grid.cs` + `.Presets.cs`, plus `RunnerParameterViewModel.cs` extracted to its own file). Each partial owns a disjoint set of private fields; cross-partial invalidation happens through a coordinator method on the core partial (e.g. `InvalidateAll` for Monte Carlo, `RecomputeCombinations` for the Runner).

## 8a. GUI Shell & Design Tokens

- **Standard WPF window chrome.** `MainWindow.xaml` uses standard WPF window with `WindowStartupLocation="CenterScreen"` and `WindowState="Maximized"`. The OS title bar is drawn by WPF; no custom chrome.
- **Menu bar.** A `Menu` inside the title bar area, VS Code-style. Items without implementations are present as `IsEnabled="False"` placeholders so the menu structure is legible. Navigation items bind to commands in `MainViewModel.Shell.cs` — they set `SelectedNode` and nothing else. Window operations (Minimize, Exit) are `Click` handlers in `MainWindow.xaml.cs` since they need the Window instance.
- **Left navigation tree.** A `TreeView` on the left, width 200, `ItemsSource` bound to `MainViewModel.NavigationNodes` and `SelectedItem` two-way bound to `MainViewModel.SelectedNode`. Root nodes map to top-level tabs; child nodes represent subtabs of a parent (Runner to Runner | Optimization Results). Each `NavigationNode` carries a `Func<object>` factory that lazily resolves the target tab ViewModel and an optional `SubtabIndex` for child nodes. Selecting a node sets `MainViewModel.SelectedNode`, which the `ContentControl` template-swaps via `DataTemplate` in `App.xaml`. Selecting a child additionally invokes `INavigationTarget.Navigate(subtabIndex)` on the resolved ViewModel; the parent's `SelectedSubtabIndex` flips, and its internal visibility toggles.
- **Status bar.** Two-cell bottom bar. Left cell binds `MainViewModel.StatusText`. Right cell is a static placeholder reserved for a future contextual value (active account, queue count). Per-tab status lines (Data, Runner, MT5) remain inside their own views — the shell status bar is a separate strip, not a replacement.
- **DPI awareness.** WPF handles DPI scaling automatically via `dpiAware` in manifest. ScottPlot controls refresh on window DPI change.
- **Design tokens.** `App.xaml` defines a `ResourceDictionary` of `SolidColorBrush` keys: `BackgroundBrush` (#181818), `PanelBrush` (#222222), `TextPrimaryBrush` (#E6EDF3), `TextSecondaryBrush` (#848D97), `AccentBrush` (#2F81F7), `WfGood` (#2ECC71), `WfWarn` (#F5A623), `WfBad` (#E74C3C), `BorderBrush` (#333333). Every shell element uses `{StaticResource TokenName}` — never hardcoded hex.
- **`MainViewModel.Shell.cs`** is a partial-class file holding only navigation commands (one per tab). No DI graph, no state, no logic — pure `SelectedNode = _xxxNode` setters.
- **Theme configuration.** `AppSettings` carries theme color ARGB hex strings, defaulted in `ConfigManager.LoadConfig`. Colors are applied via `App.xaml` StaticResource brushes. No runtime theme applier needed — WPF StaticResource lookup handles it.
- **Color picker dialog.** `src/gui/Config/ColorPickerWindow.xaml` is a modal `Window` with a custom color spectrum/ring UI. `ConfigView.xaml.cs` opens it via `ShowDialog<bool>`, and on OK applies the result to the `SettingItem`.
- **Config tab layout.** `SettingItem`s whose name contains "Color" are routed into `ConfigViewModel.ThemeSettings`; everything else stays in `DynamicSettings`. `ConfigView.xaml` renders the theme collection inside a collapsed `Expander` ("Theme Colors") above the Provider Order section.
- **Resolution target.** 1080p @ 100% scaling is the reference. Grid rows 26px, base text 12px, panel headers 16px, status bar 11px.

## 9. Tabs
- Overview — last-run summary with per-dataset table and aggregate footer.
- Runner — strategy catalog, parameter editor, source preview, preset save/load, run button. Three subtabs: Runner (strategy + parameters), Optimization Results, Walk-Forward Results.
- Monte Carlo — per-dataset outcome probabilities, terminal-balance/max-DD histograms, equity curves, trade clusters. See §14.
- Metrics — metric catalog browser with DataGrid.
- Accounts — prop-firm rule browser and active selection.
- Data — Grouped datasheet: one bold header row per symbol (label `{Symbol}   ({N})`), its timeframes nested underneath in bar-interval-descending order (1W → 1m). Column filters and sort indicators on every column; only the Symbol header drives group order (asc/desc). Fetch form (symbol + timeframe + provider) for cache-miss ingestion; two actions: Fetch (diagnostic) and Fetch & Queue (workflow).
  - Raw Ticks / Timeframes subtabs (2026-10-28). Data tab splits in two. Raw Ticks holds tick CSVs under data/ticks plus cache envelopes whose DataOrigin is RawTick (bars compiled from those CSVs, source MT5 Parsed). Timeframes holds TimeframeFetch entries from network providers. Both sides remain queuable; IQueueOriginGate warns when Runner or Chart is asked to consume a raw-tick entry until compile-on-demand lands. Default subtab on open is Raw Ticks. Ctrl+C on a selected row copies its cache path. Raw Ticks carries a compile form (tick CSV -> bars) driven by ITickCompiler.
- Data Automation — three subtabs (Extraction / Parsing / Analysis). See §18.
- MT5 Ticks — raw tick streaming from MetaTrader 5, broker-side history sync into the local `.tkc` cache, direct `.tkc` decoding into kline cache, and tick-CSV parsing into kline cache. Four subtabs: **Extraction** (fetch + Verify Range panel), **Requester** (SYNC_TICKS_RANGE), **.tkc Extract** (local .tkc files → kline cache), **Parsing** (tick CSV → kline cache). See §19 and §20.
- Providers — Collapsible per-provider cache diagnostics. See §16.
- Chart — ScottPlot.WPF 5.1.59 candlestick + volume via secondary axis. Keyboard-only navigation: ScottPlot's `UserInputProcessor` is disabled on the plot; arrow keys pan 10% of the visible span per press, `+`/`-` zoom the X-axis by 10% per press. PointerMoved drives the crosshair readout; the plot's context menu retains Auto/Percent/Indexed-to-100. The Chart tab is also the target of cross-tab navigation from the Monte Carlo cluster plot (see §14) and reacts to `NavigateToTradeMessage` by selecting the queued dataset and setting a 200-bar X-axis window centered on the requested timestamp.
- Logs — event-driven live log view with command input.
- Config — auto-generated settings editor, dynamic provider discovery and ordering list.

## 10. Logging

- LogManager is a **static class** in `src/core/LogManager.cs`. It is the sole static in the codebase, and the only exception to the "no static globals" rule — it must initialize before the DI container exists.
- Bootstrap: `Program.Main` calls `LogManager.Initialize()` before `new App()`. The log file is open and writable before any WPF XAML loads.
- Crash safety: the entire `Main` body is wrapped in try/catch. Any exception at process level is written to the log with the `[App]` tag, then rethrown so the process exits non-zero.
- Tag enum `LogTag`: `App`, `Cache`, `Data`, `Provider`, `Strategy`, `Runner`, `Account`, `MonteCarlo`, `Overview`, `Chart`, `Config`, `Metric`. Providers use a single `Provider` tag; the provider name is embedded in the message. MT5 tick data also logs under `Provider`.
- Severity levels:
  - `Info` — main flow. Service started, strategy executed, cache saved, account activated, provider returned data.
  - `Debug` — troubleshooting. RAM hit/miss, per-attempt traces, compile cache hits, firm expansion counts, provider discovery, MT5 wire traffic.
  - `Warn` — recoverable abnormal. Provider fallback triggered, cache miss on a file that should have existed, stale settings entry.
  - `Error` — exception or failure. Always carries the `Exception` object when available.
- No filtering. Every log line goes to the ring buffer and the file, regardless of level. `SetMinimumLevel` is a no-op kept for API compatibility.
- Output format:
  `{Timestamp:yyyy-MM-dd HH:mm:ss.fff} [{Level:u3}] [{Tag}] {Message:lj}{NewLine}{Exception}`
  Example: `2026-09-26 14:09:17.053 [INF] [App] Process start`
- File sink: `logs/runtime-{yyyyMMdd-HHmmss}.log`, one per app run, via Serilog with `flushToDiskInterval: 500ms`.
- In-memory ring buffer: 1000 entries. Exposed via `GetRecentEntries()`. Feeds the Logs tab on startup and every time the view is constructed.
- `Logged` event: fires after every write with a `LogEntry(Timestamp, Level, Tag, Message)`. `LogsViewModel` subscribes and marshals to the UI thread.
- `CurrentLogFilePath`: public property giving the current run's log file path. The Logs tab's "Refresh from File" reads this file with `FileShare.ReadWrite | FileShare.Delete` so Serilog's writer is never blocked.
- Call-site convention: `LogManager.Info(LogTag.X, "message")`, `LogManager.Warn(...)`, `LogManager.Debug(...)`, `LogManager.Error(LogTag.X, "message", ex)`. No instance is ever passed. No service receives `LogManager` in its constructor.

## 11. Runner & Presets

- **Layout.** `RunnerView.xaml` has a toolbar row (queue hint, Refresh Catalog, Run, Optimize, Cancel, progress bar, ETA, Presets, Save Preset, combination summary), two collapsible expanders (**MC Settings** and **WF Settings**), and three visibility-toggled panels for subtabs: **Runner** (strategy + parameters), **Optimization Results** (batch grid), **Walk-Forward Results** (window grid). A read-only status line runs along the bottom.
- **Runner subtab.** Three-column layout via `GridSplitter`: strategy list left (filter TextBox + `ListBox`), parameter panel right. The source-preview column was removed — source is opened on demand via the `View Source...` button, which raises `ViewSourceRequested` and opens `StrategySourceWindow` (`src/gui/Strategy/`). The parameter panel is populated by `StrategyParameterParser.Parse(source)` and lists one row per `// @param`. Numeric params (`int` / `double`) show three extra fields (Min / Max / Step) that drive the optimizer grid; string-with-options params show a ComboBox; bool params show a CheckBox.
- **Optimization Results subtab.** DataGrid listing every version from every batch file under `data/optimizations/`, aggregated across all runs. Columns: Version, Net PnL, Trades, Win %, Profit Factor, Max DD, Sharpe, Objective, MC Ran, Time. Sort key is selectable from a dropdown (default `EdgePercent`; every scalar on `McSummary` plus `NetPnl`, `Cagr`, `TradeCount`, `BacktestDurationMs` is available). A `NumericUpDown` caps the visible rows (default 100). Refresh re-reads the directory; Open Folder opens `data/optimizations/` in Explorer.
- **Walk-Forward Results subtab.** DataGrid listing every window from loaded walk-forward batches. Columns: Window, IS Start, IS End, OOS Start, OOS End, IS Objective, OOS Objective, Degradation, Trades, Net PnL, Status. RowStyle highlights start bars (bold) and successful windows (green). Context menu: View Source, Re-run Window.
- **RunnerViewModel** — split into partial-class files under `src/gui/Runner/` per the §8 partial-class convention:
  - `RunnerViewModel.cs` — core: DI fields (including `OptimizationStore _optStore`), observable collections, `[ObservableProperty]` fields, `_optimizeCts`, `_suppressGridPersist`, `RunCompleted` / `ViewSourceRequested` events, constructor, lifecycle and selection handlers (`OnActiveAccountChanged`, `ApplyAccountDefaults`, `OnCatalogChanged`, `SyncCatalog`, `OnStrategyFilterChanged`, `ApplyFilter`, `OnQueueChanged`, `SyncQueue`, `OnSelectedStrategyChanged`, `OnIsBusyChanged`, `LoadStrategyAsync`, `RefreshCatalog`, `ViewSourceAsync`), and shared UI helpers (`ConfirmAsync`, `GetMainWindow`).
  - `RunnerViewModel.Run.cs` — `RunAsync` and the per-dataset batch loop.
  - `RunnerViewModel.Optimize.cs` — `OptimizeAsync`, `CancelOptimize`, `BuildGridParameters`, `FormatEta`.
  - `RunnerViewModel.Grid.cs` — `OnParametersCollectionChanged`, `OnParameterPropertyChanged`, `GridKey`, `PersistGridRange`, `PersistAllGridRanges`, `RecomputeCombinations`, `Cap`.
  - `RunnerViewModel.Presets.cs` — `OpenPresetsAsync`, `SavePresetAsync`, `ApplyPreset`, `PromptForNameAsync`.
  - `RunnerViewModel.Results.cs` — `RefreshResultsAsync`, `OpenResultsFolder`, `ApplyResultsSort`, the `OptimizationResults` collection, `ResultsSortKey` / `ResultsCap` / `ResultsStatus` / `IsResultsExpanded` observable properties, and the `_allResults` aggregate list.
  - `RunnerParameterViewModel.cs` — the `RunnerParameterViewModel` class.
  - `OptimizationResultRow.cs` — flattened view of one `OptimizationVersion` for the DataGrid; owns the display projections (`NetPnlDisplay`, `CagrDisplay`, `PFundedDisplay`, …), the sort-key list, and the `Cagr` computation (`(1 + NetPnl / StartingCapital)^(1 / DataSpanYears) - 1`).
  - `WalkForwardWindowRow.cs` — flattened view of one `WalkForwardWindowResult` for the DataGrid; owns display projections and sort keys.
- **Toolbar.** No symbol picker — the symbol/timeframe list comes from the `SelectionQueue` (see §15). Toolbar buttons: Refresh Catalog, Run, Optimize, Cancel (visible only while optimizing), Presets..., Save Preset.... The `QueueHint` line names the queued ticker and timeframes, or shows the multi-ticker / empty-queue warning.
- **RunAsync** (in `RunnerViewModel.Run.cs`): reads current parameter values into a `Dictionary<string,string>`, iterates the queued datasets, calls `DataManager.GetSymbolDataAsync(symbol, timeframe, progress)` per dataset, invokes `StrategyManager.RunBacktestFromFileAsync(path, data, params)`, raises `RunCompleted` per dataset, and finally calls `_lastRun.PublishBatch(batchResults)`. **Run is not the same as Optimize** — Run is a single-configuration pass over the queue, no grid, no MC. Results go to `LastRunStore` (RAM, single-slot) and surface in the Overview and Monte Carlo tabs.
- **OptimizeAsync** (in `RunnerViewModel.Optimize.cs`): same queue walk, but calls `OptimizationRunner.RunAsync(path, name, symbol, timeframe, data, grid, config, progress, ct)` per dataset. Each call builds the parameter grid, backtests every combination, runs a coarse MC pass, refines the top-N survivors at higher iterations, and writes a batch file to `data/optimizations/`. The pipeline inside `OptimizationRunner.RunAsync`: `ParameterGridBuilder.Build` → parallel backtest per combination → coarse MC per version → top-N survivors by `PFunded` / `TerminalBalanceMedian` at coarse iterations → refine pass at higher iterations → `OptimizationStore.SaveAsync`. Every combination becomes an `OptimizationVersion`; every version carries its `ParameterSet[]` grid co…
- **WalkForwardAsync** (in `RunnerViewModel.WalkForward.cs`): validates plan, builds `WalkForwardRunConfig`, calls `WalkForwardRunner.RunAsync` per dataset, aggregates window results, and switches to the Walk-Forward Results subtab on completion.
- **One-ticker constraint.** Both Run and Optimize refuse to start when the queue spans more than one distinct symbol (case-insensitive). The status line names every offending ticker and points back to the Data tab. `CanRun` is false in the same condition, so the Run/Optimize buttons are disabled until the queue narrows. Single-ticker queue runs exactly the timeframes that are queued — a subset of the ticker's cached timeframes is legal and is announced on the status line as `Running <strategy> on <ticker> - N timeframe(s): ...`.
- **RunnerParameterViewModel**: wraps a `StrategyParameter` with mutable `CurrentValue` / `CurrentBool`, plus three grid-range text fields `MinText` / `MaxText` / `StepText` for numeric params. Exposes `HasOptions`, `IsTextInput`, `IsBoolToggle`, `IsNumeric`, `IsDirty`, `ValueCount`, `HasGridRange`, `EffectiveValue`, `GridRangeSerialized()` / `ApplySerializedGridRange()`. `IsDirty` is true when `EffectiveValue` differs from `DefaultValue`. `ValueCount` mirrors `ParameterGridBuilder.GetValuesFor` — 2 for `bool`, `Options.Count` for string-with-options, the range size for numeric, else 1. `HasGridRange` is gated on `IsNumeric`, so the Min/Max/Step editor never renders for bool or string-with-options even though their `ValueCount` is > 1.
- **StrategyParameterParser** (`src/core/Runner/StrategyParameterParser.cs`): parses `// @param` lines. Stops at the first non-comment, non-blank line, so params must sit **above any `using` directive**. Supports int, double, bool, string; string params may declare `options: "A", "B"` for a ComboBox.
- **TradeRecorder** (`src/core/Runner/TradeRecorder.cs`): records closed trades. Strategy calls `Advance(index)`, `Enter(side, price, size = 1.0)`, `Exit(price)`. Timestamps come from `Data[index]`. One open position at a time is enforced by `_hasOpen`.
- **PresetService** (`src/core/Runner/PresetService.cs`):
  - Preset files live in `config/presets/{StrategyName}_{PresetName}.preset.json`.
  - Filename parsing splits on the first underscore: everything before is the strategy name, everything after is the preset name.
  - `Sanitize` replaces `_` and invalid filename chars with `-` in the filename; the JSON payload retains the original name.
  - `ListForStrategy(name)` / `ListAll()` / `Load(path)` / `Save(name, preset, values, gridRanges)` / `Delete(path)` / `GetNextSequentialName(name)`.
  - Preset payload carries both the parameter values and the per-parameter grid ranges.
- **Load flow**: toolbar "Presets..." → `PresetPickerWindow` → `ApplyPreset` writes every value back into the parameter panel.