<!-- docs\ARCHITECTURE.md -->
# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: Microsoft.Extensions.DependencyInjection, managed in App.axaml.cs.
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
  - Controls are rendered dynamically in ConfigView.axaml based on data type:
    - int / double -> NumericUpDown (steppers with appropriate increments)
    - bool -> ToggleSwitch
    - string containing "Color" -> ColorPicker (ColorSpectrumShape="Ring")
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
- Each provider is registered as a keyed transient in App.axaml.cs.
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
- Legacy-format detection: LoadCacheAsync returns null on empty files, malformed JSON, or all-zero payloads, so DataManager refetches.
- Metadata parsing: `CacheManager.ReadCacheMetadata` scans the JSON payload for any non-zero volume to expose `HasVolume` for the Data tab without fully deserializing the file into memory. Also collects running `MinT`/`MaxT` in the same pass — does not assume ascending bar order.
- Per-file log lines: "Cache: saved {key} ({n} bars, {ms} ms)" and "Cache: loaded {key} ({n} bars, {ms} ms)".
- Memory-tier observability: DataManager logs "RAM hit" / "RAM miss" for every lookup.
- Benchmark: CacheManager.BenchmarkAsync(int barCount) serializes and loads N synthetic bars, logging elapsed milliseconds.

## 8. GUI (MVVM Toolkit)

- **Shell navigation tree (formerly tab host pattern, Phase 17).** `MainWindow.axaml` hosts a left-docked `TreeView` bound to `MainViewModel.NavigationNodes`, with `SelectedItem` two-way bound to `MainViewModel.SelectedNode`. The content host is a sibling `ContentControl` bound to `MainViewModel.CurrentVm`. Tab content is never declared inline — the tree carries only header labels, and the `ContentControl` template-swaps the View via `App.axaml`'s `DataTemplate` entries. No tab's View or ViewModel is materialized until its node is selected.
- **Tab ViewModel resolution (Phase 17).** `MainViewModel` takes `IServiceProvider` in its constructor and exposes 12 lazily-resolved tab properties (`OverviewVm`, `RunnerVm`, `MonteCarloVm`, `MetricsVm`, `AccountVm`, `DataVm`, `DataAutomationVm`, `Mt5TickVm`, `ProvidersVm`, `ChartVm`, `LogsVm`, `ConfigVm`). Each is `_xxx ??= _sp.GetRequiredService<XxxViewModel>()`, so the DI resolve fires exactly once, on first access. `OnSelectedNodeChanged` invokes the selected node's `Func<object>` factory (which routes to one of these properties) and assigns `CurrentVm`, which raises `PropertyChanged(nameof(CurrentVm))` so the `ContentControl` re-evaluates on tree selection.
- **VM → View mapping (Phase 17).** `App.axaml` declares `<Application.DataTemplates>` with one `<DataTemplate DataType="vm:XxxViewModel"><views:XxxView /></DataTemplate>` per tab. Avalonia resolves the DataTemplate when `CurrentVm` is set, on tab switch. Views never resolve their own ViewModel and never read `Application.Current.Services`.
- **View code-behind (Phase 17).** `InitializeComponent()` only, plus View-local wiring that does not depend on a specific ViewModel instance (event handlers, `UserInputProcessor.Disable()` calls, tunnelled `KeyDown` handlers). DataContext-dependent wiring runs from `OnDataContextChanged` — e.g. `ChartView` hands its `AvaPlot` to `ChartViewModel.SetPlotControl`, `MonteCarloView` hands the three plots to the corresponding setters, `DataView` subscribes to `OpenQueueRequested`, `DataAutomationView` subscribes to the three calibration events. The old `if (Application.Current is App app) DataContext = app.Services.GetRequiredService<TViewModel>();` pattern is forbidden in View constructors.
- **Startup path (Phase 17).** `Program.Main` → `LogManager.Initialize()` → `BuildAvaloniaApp().StartWithClassicDesktopLifetime(...)` → `App.Initialize` (XAML load) → `App.OnFrameworkInitializationCompleted` (DI container build, `ConfigManager` resolve, `Mt5TickServer.Start()`, `MainViewModel` resolve, `MainWindow` construct). All five stages are instrumented with `Stopwatch` markers logging `[startup] ...` Info lines. Only tab 0's ViewModel (Overview) is constructed at startup — it is `CurrentVm` at first render. Every other tab's ViewModel is constructed on first selection. Measured cold start after Phase 17: ~530 ms (was ~3324 ms).
- Source Generation: All ViewModels use CommunityToolkit.Mvvm attributes ([ObservableProperty], [RelayCommand]).
- XAML Bindings: x:DataType on every View/UserControl root.
- DataGrid use: Avalonia.Controls.DataGrid 11.0.11.
- ColorPicker use: Avalonia.Controls.ColorPicker 11.0.11 with `ColorSpectrumShape="Ring"`.
- Selectable text policy: TextBlock for clickable list rows; read-only TextBox for source preview, log output, status line.
- RelativeSource binding inside DataTemplates: use `{Binding DataContext.Prop, RelativeSource={RelativeSource AncestorType=UserControl}}`. Do not use `$parent[UserControl].((vm:Type)DataContext).Prop`.
- ViewModel partial-class convention: where a tab's ViewModel grows past ~600 lines it is split into partial-class files alongside the core file. Precedent: `Mt5TickViewModel` (core + `.Extraction.cs` + `.Requester.cs` + `.Parsing.cs` + `.Tkc.cs`), `MonteCarloViewModel` (core + `.Plots.cs` + `.Stats.cs` + `.Cluster.cs`), and `RunnerViewModel` (core + `.Run.cs` + `.Optimize.cs` + `.Grid.cs` + `.Presets.cs`, plus `RunnerParameterViewModel.cs` extracted to its own file). Each partial owns a disjoint set of private fields; cross-partial invalidation happens through a coordinator method on the core partial (e.g. `InvalidateAll` for Monte Carlo, `RecomputeCombinations` for the Runner).

## 8a. GUI Shell & Design Tokens

- **Custom chrome.** `MainWindow.axaml` uses `ExtendClientAreaToDecorationsHint="True"` + `ExtendClientAreaChromeHints="NoChrome"` + `ExtendClientAreaTitleBarHeightHint="32"`. The OS title bar is not drawn; the app renders its own 32px title bar containing the menu and window controls.
- **Fullscreen.** `WindowState.FullScreen` is applied from `MainWindow.Opened` and re-applied from `Activated` whenever the window returns to `Normal` (taskbar restore path). Not `Maximized` — on Windows 11, Maximized combined with ExtendClientArea leaves DWM rounded corners and a caption shadow on the window. `SystemDecorations="Full"` + `NoChrome` avoids both. `CanResize="False"` prevents Windows Snap. Alt+F4, Alt+Tab, and minimize-to-taskbar all work normally.
- **Menu bar.** A `Menu` inside the custom title bar, VS Code-style. Items without implementations are present as `IsEnabled="False"` placeholders so the menu structure is legible. Navigation items bind to commands in `MainViewModel.Shell.cs` — they set `SelectedNode` and nothing else. Window operations (Minimize, Exit) are `Click` handlers in `MainWindow.axaml.cs` since they need the Window instance.
- **Left navigation tree.** A `TreeView` on the left, width 220, `ItemsSource` bound to `MainViewModel.NavigationNodes` and `SelectedItem` two-way bound to `MainViewModel.SelectedNode`. Root nodes map to top-level tabs; child nodes represent subtabs of a parent (Runner to Runner | Optimization Results). Each `NavigationNode` carries a `Func<object>` factory that lazily resolves the target tab ViewModel and an optional `SubtabIndex` for child nodes. Selecting a node sets `MainViewModel.CurrentVm`, which the `ContentControl` template-swaps. Selecting a child additionally invokes `INavigationTarget.Navigate(subtabIndex)` on the resolved ViewModel; the parent's `SelectedSubtabIndex` flips, and its internal `TabControl` (strip hidden via a `/template/ ItemsPresenter` style) binds `SelectedIndex` to that property. Cross-tab navigation (`NavigateToTradeMessage`) and every menu command target tree nodes by reference, not by index.
- **Status bar.** Two-cell bottom bar. Left cell binds `MainViewModel.StatusText`. Right cell is a static placeholder reserved for a future contextual value (active account, queue count). Per-tab status lines (Data, Runner, MT5) remain inside their own views — the shell status bar is a separate strip, not a replacement.
- **Design tokens.** `src/gui/Theme/Tokens.axaml` is a `ResourceDictionary` of `SolidColorBrush`, `x:Double`, `FontFamily`, and `CornerRadius` entries. Merged into `App.axaml` via `<ResourceInclude Source="avares://backs2/src/gui/Theme/Tokens.axaml"/>`. Every shell element uses `{DynamicResource TokenName}` — never hardcoded hex.
- **`MainViewModel.Shell.cs`** is a partial-class file holding only navigation commands (one per tab). No DI graph, no state, no logic — pure `SelectedNode = _xxxNode` setters.
- **Theme configuration.** `AppSettings` carries 14 `Theme*Color` ARGB hex strings (`ThemeAppBgColor`, `ThemeAccentColor`, etc.), defaulted in `ConfigManager.LoadConfig` for backward compatibility with pre-theme `settings.json`. `src/gui/Theme/ThemeApplier.cs` parses each string with `Avalonia.Media.Color.TryParse` and writes a `SolidColorBrush` into `Application.Current.Resources[key]` for the matching token key. Called from `App.axaml.cs` right after `ConfigManager` resolves and again from `ConfigViewModel.ApplyThemePreview` when the user clicks OK in the color picker.
- **Color picker dialog.** `src/gui/Config/ColorPickerWindow.axaml` is a modal `Window` built from the color primitives (`ColorSpectrum`, `ColorSlider`, `ColorPreviewer`) — not `ColorPicker` / `ColorView`, which exhibited first-click lazy-load freezes and rendering failures in Avalonia 11.0.x (discussions #9128 / #9098). `ConfigView.axaml.cs` opens it via `ShowDialog<bool>`, and on OK applies the result to the `SettingItem` and calls `ConfigViewModel.ApplyThemePreview`. The shell recolors live on OK; persistence still requires the Save Settings button.
- **Config tab layout.** `SettingItem`s whose name contains "Color" are routed into `ConfigViewModel.ThemeSettings`; everything else stays in `DynamicSettings`. `ConfigView.axaml` renders the theme collection inside a collapsed `Expander` ("Theme Colors") above the Provider Order section, so the flat engine settings are not visually swamped by 14 color rows.
- **Resolution target.** 1080p @ 100% scaling is the reference. Grid rows 26px, base text 12px, panel headers 16px, status bar 11px.

## 9. Tabs
- Overview — last-run summary with per-dataset table and aggregate footer.
- Runner — strategy catalog, parameter editor, source preview, preset save/load, run button.
- Monte Carlo — per-dataset outcome probabilities, terminal-balance/max-DD histograms, equity curves, trade clusters. See §14.
- Metrics — metric catalog browser.
- Accounts — prop-firm rule browser and active selection.
- Data — Grouped datasheet: one bold header row per symbol (label `{Symbol}   ({N})`), its timeframes nested underneath in bar-interval-descending order (1W → 1m). Column filters and sort indicators on every column; only the Symbol header drives group order (asc/desc). Fetch form (symbol + timeframe + provider) for cache-miss ingestion; two actions: Fetch (diagnostic) and Fetch & Queue (workflow).
- Data Automation — three subtabs (Extraction / Parsing / Analysis). See §18.
- MT5 Ticks — raw tick streaming from MetaTrader 5, broker-side history sync into the local `.tkc` cache, direct `.tkc` decoding into kline cache, and tick-CSV parsing into kline cache. Four subtabs: **Extraction** (fetch + Verify Range panel), **Requester** (SYNC_TICKS_RANGE), **.tkc Extract** (local .tkc files → kline cache), **Parsing** (tick CSV → kline cache). See §19 and §20.
- Providers — Collapsible per-provider cache diagnostics. See §16.
- Chart — ScottPlot.Avalonia 5.0.55 candlestick + volume via secondary axis. Keyboard-only navigation: ScottPlot's `UserInputProcessor` is disabled on the plot; arrow keys pan 10% of the visible span per press, `+`/`-` zoom the X-axis by 10% per press. PointerMoved drives the crosshair readout; the plot's context menu retains Auto/Percent/Indexed-to-100. The Chart tab is also the target of cross-tab navigation from the Monte Carlo cluster plot (see §14) and reacts to `NavigateToTradeMessage` by selecting the queued dataset and setting a 200-bar X-axis window centered on the requested timestamp.
- Logs — event-driven live log view.
- Config — auto-generated settings editor, dynamic provider discovery and ordering list.

## 10. Logging

- LogManager is a **static class** in `src/core/LogManager.cs`. It is the sole static in the codebase, and the only exception to the "no static globals" rule — it must initialize before the DI container exists.
- Bootstrap: `Program.Main` calls `LogManager.Initialize()` before `BuildAvaloniaApp().StartWithClassicDesktopLifetime(...)`. The log file is open and writable before any Avalonia XAML loads.
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

- **Layout.** `RunnerView.axaml` has a toolbar row (queue hint, Refresh Catalog, Run, Optimize, Cancel, progress bar, ETA, Presets, Save Preset, combination summary), a collapsible **Monte Carlo Settings** expander (iterations, block size, ruin threshold %, active account name — shared by both subtabs), and a `TabControl` with two subtabs: **Runner** (strategy + parameters) and **Optimization Results** (batch grid). A read-only status line runs along the bottom.
- **Runner subtab.** Three-column layout via `GridSplitter`: strategy list left (filter TextBox + `ListBox`), parameter panel right. The source-preview column was removed in Phase 26D.2a — source is opened on demand via the `View Source...` button, which raises `ViewSourceRequested` and opens `StrategySourceWindow` (`src/gui/Strategy/`). The parameter panel is populated by `StrategyParameterParser.Parse(source)` and lists one row per `// @param`. Numeric params (`int` / `double`) show three extra fields (Min / Max / Step) that drive the optimizer grid; string-with-options params show a ComboBox; bool params show a ToggleSwitch.
- **Optimization Results subtab.** DataGrid listing every version from every batch file under `data/optimizations/`, aggregated across all runs. Columns: Batch, Dataset, Params, Trades, Net PnL, CAGR, P(Funded), P(Ruin), P(Timeout), Edge %, Med Bal, Med DD, BestDay. Sort key is selectable from a dropdown (default `EdgePercent`; every scalar on `McSummary` plus `NetPnl`, `Cagr`, `TradeCount`, `BacktestDurationMs` is available). A `NumericUpDown` caps the visible rows (default 100). Refresh re-reads the directory; Open Folder opens `data/optimizations/` in Explorer.
- **RunnerViewModel** — split into partial-class files under `src/gui/Runner/` per the §8 partial-class convention:
  - `RunnerViewModel.cs` — core: DI fields (including `OptimizationStore _optStore`), observable collections, `[ObservableProperty]` fields, `_optimizeCts`, `_suppressGridPersist`, `RunCompleted` / `ViewSourceRequested` events, constructor, lifecycle and selection handlers (`OnActiveAccountChanged`, `ApplyAccountDefaults`, `OnCatalogChanged`, `SyncCatalog`, `OnStrategyFilterChanged`, `ApplyFilter`, `OnQueueChanged`, `SyncQueue`, `OnSelectedStrategyChanged`, `OnIsBusyChanged`, `LoadStrategyAsync`, `RefreshCatalog`, `ViewSourceAsync`), and shared UI helpers (`ConfirmAsync`, `GetMainWindow`).
  - `RunnerViewModel.Run.cs` — `RunAsync` and the per-dataset batch loop.
  - `RunnerViewModel.Optimize.cs` — `OptimizeAsync`, `CancelOptimize`, `BuildGridParameters`, `FormatEta`.
  - `RunnerViewModel.Grid.cs` — `OnParametersCollectionChanged`, `OnParameterPropertyChanged`, `GridKey`, `PersistGridRange`, `PersistAllGridRanges`, `RecomputeCombinations`, `Cap`.
  - `RunnerViewModel.Presets.cs` — `OpenPresetsAsync`, `SavePresetAsync`, `ApplyPreset`, `PromptForNameAsync`.
  - `RunnerViewModel.Results.cs` — `RefreshResultsAsync`, `OpenResultsFolder`, `ApplyResultsSort`, the `OptimizationResults` collection, `ResultsSortKey` / `ResultsCap` / `ResultsStatus` / `IsResultsExpanded` observable properties, and the `_allResults` aggregate list.
  - `RunnerParameterViewModel.cs` — the `RunnerParameterViewModel` class.
  - `OptimizationResultRow.cs` — flattened view of one `OptimizationVersion` for the DataGrid; owns the display projections (`NetPnlDisplay`, `CagrDisplay`, `PFundedDisplay`, …), the sort-key list, and the `Cagr` computation (`(1 + NetPnl / StartingCapital)^(1 / DataSpanYears) - 1`).
- **Toolbar.** No symbol picker — the symbol/timeframe list comes from the `SelectionQueue` (see §15). Toolbar buttons: Refresh Catalog, Run, Optimize, Cancel (visible only while optimizing), Presets..., Save Preset.... The `QueueHint` line names the queued ticker and timeframes, or shows the multi-ticker / empty-queue warning.
- **RunAsync** (in `RunnerViewModel.Run.cs`): reads current parameter values into a `Dictionary<string,string>`, iterates the queued datasets, calls `DataManager.GetSymbolDataAsync(symbol, timeframe, progress)` per dataset, invokes `StrategyManager.RunBacktestFromFileAsync(path, data, params)`, raises `RunCompleted` per dataset, and finally calls `_lastRun.PublishBatch(batchResults)`. **Run is not the same as Optimize** — Run is a single-configuration pass over the queue, no grid, no MC. Results go to `LastRunStore` (RAM, single-slot) and surface in the Overview and Monte Carlo tabs.
- **OptimizeAsync** (in `RunnerViewModel.Optimize.cs`): same queue walk, but calls `OptimizationRunner.RunAsync(path, name, symbol, timeframe, data, grid, config, progress, ct)` per dataset. Each call builds the parameter grid, backtests every combination, runs a coarse MC pass, refines the top-N survivors at higher iterations, and writes a batch file to `data/optimizations/`. The pipeline inside `OptimizationRunner.RunAsync`: `ParameterGridBuilder.Build` → parallel backtest per combination → coarse MC per version → top-N survivors by `PFunded` / `TerminalBalanceMedian` at coarse iterations → refine pass at higher iterations → `OptimizationStore.SaveAsync`. Every combination becomes an `OptimizationVersion`; every version carries its `ParameterSet[]` grid coordinate, backtest scalars (`TradeCount`, `NetPnl`, `BacktestDurationMs`), the coarse `McSummary`, `TerminalBalances[]`, and a downsampled representative equity curve. Edge % is computed only on the refined survivors and only when the version has 30+ trades.
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
- **PresetPickerWindow** (`src/gui/Runner/PresetPickerWindow.axaml`): modal Window with a DataGrid of `PresetDescriptor` rows, a "Show all strategies" checkbox, Load / Delete / Cancel buttons. Returns the loaded `Preset` via `ShowDialog<Preset?>`.
- **Save flow**: toolbar "Save Preset..." or per-parameter "Save" button (visible only when `IsDirty`) → inline name prompt (default = `GetNextSequentialName`) → `PresetService.Save`.
- **Load flow**: toolbar "Presets..." → `PresetPickerWindow` → `ApplyPreset` writes every value back into the parameter panel.
## 12. Accounts

- Purpose: model prop-firm rules (FTMO, Maventrading, etc.) so Monte Carlo and Overview can read the active account's drawdown limits.
- Data location: `data/accounts/*.account.json`. Engine-managed, version-controlled. **One file per firm**, not per size.
- Core types (`src/core/Accounts/`):
  - `AccountPhase` — one challenge/funded phase. `PhaseName`, `ProfitTargetPercent`, `MaxLossPercent`, `MaxDailyLossPercent`, `MinTradingDays`, `TradingPeriodDays`, `BestDayRulePercent`, `ProfitSplitPercent`, `Refundable`, `DrawdownType`. All rule fields nullable.
  - `FirmConfig` — top-level JSON record: `Firm`, `Currency`, `Notes`, `Plans`.
  - `FirmPlan` — `Plan` (display name), `Sizes`.
  - `PlanSize` — `Size` (display label), `StartingCapital`, `Phases`, `ConsistencyScoreRule`, `Notes`.
  - `AccountJsonContext` — source-generated `System.Text.Json` context for `FirmConfig`. Camel-case, write-indented, nulls omitted.
  - `AccountDescriptor` — `FilePath`, `FileStem`, `Firm`, `Plan`, `Size`, `StartingCapital`. One descriptor per (plan, size). Produced by expanding a firm JSON.
  - `AccountService` — singleton. Scans `data/accounts/` for `*.account.json`, parses each into a `FirmConfig`, caches it, expands into `AccountDescriptor` entries. `ResolvePlanSize(descriptor)` returns the `PlanSize` for a selection. `SetActive(descriptor)` writes the triple `ActiveAccountFirmFile` / `ActiveAccountPlan` / `ActiveAccountSize` into `config/settings.json` and raises `ActiveChanged`.
- Active selection is a **triple** (firm file stem, plan, size). `AppSettings` carries three string fields.
- No FileSystemWatcher. Manual Refresh button on the Accounts tab. Accounts change rarely.
- GUI (`src/gui/Accounts/`):
  - `AccountView.axaml` — three-column grid. Left: `ListBox` of descriptors, `Firm` bold with `Plan · Size` subtitle. Right: detail pane rendering the resolved `PlanSize.Phases` as label/value rows.
  - `AccountViewModel` — `Accounts`, `PhaseRules`, `SelectedAccount`, `ActiveAccountName`, `HasSelection`. Commands: `RefreshCommand`, `SetActiveCommand`, `OpenFolderCommand`.
  - `AccountRuleRow` — `Label`, `Value` record.
- Tab position: `MainWindow.axaml` places Accounts between Metrics and Data.
- Template: `data/accounts/_TEMPLATE.account.json` documents every field with placeholder values. Copy it to add a new firm. To exclude it from the scan, rename it to `.account.json.txt`.
- Config tab interaction: the three `ActiveAccount*` string properties auto-render as TextBoxes in the Config tab. Editing them is not equivalent to `SetActive` — the tab's `Set Active` button is the only supported path that also reloads and caches.

## 13. Overview & LastRunStore

- Purpose: hand off the most recent strategy run's trades and source data to any consumer (Overview tab, Monte Carlo engine, future analytics), and surface a per-dataset summary of the most recent batch.
- `OverviewData` record (`src/core/Overview/OverviewData.cs`):
  - `StrategyName`, `StrategyPath` — from the selected `StrategyDescriptor`.
  - `Symbol`, `Timeframe` — the run context.
  - `RunUtc`, `Duration` — from `StrategyRunResult`.
  - `Trades` — `IReadOnlyList<Trade>` from `StrategyRunResult.Trades`.
  - `SourceData` — the `List<Kline>` fetched for the run. Required by Monte Carlo.
  - `Error` — null on success, exception message on failure. Failed runs still publish so consumers can display the error state.
- `LastRunStore` singleton (`src/core/Overview/LastRunStore.cs`):
  - `Batch` — `IReadOnlyList<OverviewData>` for the most recent run cycle. One entry per queued dataset.
  - `Current` — the last entry in `Batch`, or null.
  - `Publish(data)` — replaces `Batch` with a single-entry list, raises `Changed`.
  - `PublishBatch(batch)` — replaces `Batch`, raises `Changed` with the new list.
  - `Changed` — event carrying `IReadOnlyList<OverviewData>`. Consumers subscribe and marshal to the UI thread with `Dispatcher.UIThread.Post`.
  - Lock-protected with a private `object _gate`. No async, no queue — single-slot store.
- Publisher: `RunnerViewModel.RunAsync` — after iterating the queue, `_lastRun.PublishBatch(batchResults)` is called once with every dataset's result. Both success and failure paths publish.
- Consumer: `OverviewViewModel` — subscribes to `Changed` in the constructor and reads `Batch` once at construction. Rebuilds a per-dataset table (`OverviewRow`) plus an `OverviewAggregate` on every change.
- Aggregate semantics: `TotalTrades` and `TotalNetPnl` are sums of per-dataset values. `WeightedWinRatePercent` is the trade-count-weighted mean of per-dataset win rates. No trade pooling across datasets.
- Lifetime: process-scoped. Not persisted across app restarts. Each new run replaces the batch.

## 14. Monte Carlo Tab

- Purpose: evaluate the most recent strategy run against prop-firm rules using the time-domain block-bootstrap engine (see §17). Produces per-dataset outcome probabilities, terminal-balance and max-drawdown distributions, equity curves, and a trade-cluster view.
- GUI (`src/gui/MonteCarlo/`):
  - `MonteCarloView.axaml` — Grid with four rows: collapsible settings expander (Row 0), source info banner (Row 1), main content (Row 2), status line (Row 3). Main content is `ColumnDefinitions="440,6,*"`: stats rail left, splitter, plot pane right. The plot pane hosts three `AvaPlot` controls (`HistogramPlot`, `CurvesPlot`, `ClusterPlot`) stacked in a single Grid cell and toggled by `IsVisible` bindings (`IsHistogramVisible` / `IsCurvesVisible` / `IsClusterVisible`). The toggle avoids ScottPlot rebuilding the render tree on every mode change.
  - `MonteCarloView.axaml.cs` — resolves `MonteCarloViewModel` via DI, wires the three plots through `SetHistogramPlotControl` / `SetCurvesPlotControl` / `SetClusterPlotControl`. Disables `AvaPlot.UserInputProcessor` on all three (see Keyboard navigation) and installs a `KeyDown` handler for arrow pan and `+`/`-` zoom. Attaches the cluster plot's `PointerPressed` to `HandleClusterPlotClick`.
  - `MonteCarloViewModel` — split into four partial-class files under `src/gui/MonteCarlo/`:
    - `MonteCarloViewModel.cs` — core: DI wiring, observable properties, lifecycle subscriptions (`LastRunStore.Changed`, `AccountService.ActiveChanged`), batch orchestration (`RunAsync`, `Cancel`, `TestWorkerAsync`), field invalidation (`InvalidateAll`), view-model records (`McPlotMode`, `PlotModeOption`, `McKpiCard`, `McStatSection`, `McStatLine`, `McBatchRow`).
    - `MonteCarloViewModel.Plots.cs` — histogram and equity-curve rendering, plot theming (`ApplyPlotTheme`), downsampling helpers, `EnsureVisiblePlotRendered`, render-cache markers for the histogram and curves plots.
    - `MonteCarloViewModel.Stats.cs` — `PopulateStats`, KPI card construction, tone palette (neutral/dim/warn/good/amber/accent), `CurveMemoryLabel`.
    - `MonteCarloViewModel.Cluster.cs` — k-means dispatch, cluster plot draw, hit-testing, `NavigateTo`, cluster palette, cluster render-cache marker.
  - Partial convention: each partial owns a disjoint set of private fields. Cross-partial invalidation flows through `InvalidateAll` in the core partial, which nulls the fields owned by the other three.

- Data flow:
  - Reads `LastRunStore.Batch` for the dataset list.
  - Reads `AccountService.Active` for starting capital and funded-phase rules (`MaxLossPercent`, `MaxDailyLossPercent`, `ProfitTargetPercent`, `BestDayRulePercent`, `DrawdownType`, `TradingPeriodDays`).
  - Subscribes to `LastRunStore.Changed` and `AccountService.ActiveChanged`. Stale results are cleared on a new run.
  - `RunAsync` iterates every non-errored batch entry, builds `TimeSlice[]` via `TimeSliceBuilder.Build`, runs `MonteCarloEngine.Run` independently per dataset, stores each result in `_resultsByIndex`. No aggregation.

- Iteration cap and cancellation:
  - `MonteCarloEngine.MaxIterations = 100_000` (100× the default of 1000). Enforced in three layers: the `NumericUpDown.Maximum` caps the UI, the ViewModel refuses to start a run whose `Iterations` exceeds the cap, and the engine clamps any programmatic value.
  - `RunAsync` creates a `CancellationTokenSource`; `Cancel` (bound to the toolbar button) fires it. The token flows into `MonteCarloEngine.Run` via `ParallelOptions.CancellationToken`. Cancellation latency is bounded by one simulation's duration (~50–500 ms depending on dataset).
  - Partial results from datasets that completed before cancel are kept.
  - Both success and cancellation paths log under `LogTag.MonteCarlo`.

- Curve retention ceiling:
  - `MonteCarloEngine.MaxCurveCells = 100_000_000` (~800 MB of `double[][]` curve data at 8 bytes/cell).
  - Above the ceiling, `retainCurves` is false and no per-simulation `double[]` is allocated. All scalar stats (P(ruin), terminal-balance distribution, streaks, etc.) still populate.
  - `MonteCarloResult.CurvesRetained` carries the flag. When false, the equity-curve plot shows a retention-limit message instead of curves, and `PopulateStats` adds a `MEMORY` section explaining the drop.
  - The status line during a run flags the drop in advance: `(equity curves will be dropped — memory ceiling)`.

- Toolbar controls (settings expander): Iterations (`NumericUpDown`, default 1000, max 100,000), Mean Block Size (default 5, max 100), Ruin Threshold % (display only), Run Monte Carlo, Cancel, Test Worker, ProgressBar (visible only while running), `CurveMemoryLabel`, active account name. A hint line below describes the caps.

- Stats rail layout:
  - **Per-Dataset Outcomes** block (visible when `HasResults`): dataset selector combo (visible when `HasBatch`), column-header row (Dataset / Ruin / Funded / Timeout), then an `ItemsControl` of `McBatchRow`.
  - **KPI cards**: `UniformGrid Columns="2"` of `McKpiCard` — P(ruin), P(funded), median max DD, best-day-rule violation probability. Each card carries label, 22px value, subtitle, and a direction-brushed value.
  - **Detail sections**: `ItemsControl` of `McStatSection`. Each section renders a header bar plus label/value rows (`McStatLine`). Sections: Terminal Balance, Time to Outcome, Streaks, Ruin Breakdown (conditional), Memory (conditional).

- Plot modes (ComboBox in the plot-pane header):
  - **Terminal Balance** — 40-bin histogram of `TerminalBalances`. Vertical lines at p05 (red), median (gray), p95 (green). Bars: fill `#4FC3F7`, 1px white border.
  - **Max Drawdown** — 30-bin histogram of `MaxDrawdowns`, x-axis clipped to `max(p99 × 1.10, limit × 1.15)`. Dashed amber line at the account's max-loss limit; gray median line. Title reads `Max Drawdown Distribution — {breachPct} breach the {limit}% limit`, computed by counting simulations whose max DD ≥ the limit.
  - **Trade Clusters** — see below.
  - **Equity Curves** toggle — swaps to `CurvesPlot`. Downsampled: at most 200 curves × 1000 points each. Blown and funded outcomes are always included; timeouts are sampled evenly to fill the cap. When `CurvesRetained` is false, the plot shows the retention-limit message.

- Trade cluster plot:
  - Source: the selected `OverviewData`'s `Trades` list, independent of the MC results.
  - Clustering: `KMeansClusterer.Cluster(trades, k)` with `k = max(3, min(8, √(N/10)))`. Feature vector is 4D, Z-score standardized: side (0=Short, 1=Long), `PnLPercent × 100`, duration minutes, entry hour-of-day.
  - Rendering: one `Scatter` series per cluster (palette-cycled, `FilledCircle`, marker size 6) plus a large `OpenDiamond` at each cluster's geometric centroid. X-axis is entry time (DateTime ticks); Y-axis is `PnLPercent × 100`.
  - Click handling (`HandleClusterPlotClick`): pixel coordinates translate to plot coordinates via `Plot.GetCoordinates`; the handler then finds the nearest centroid within a 40-pixel threshold, falling back to the nearest individual trade. Dispatches `WeakReferenceMessenger.Default.Send(new NavigateToTradeMessage(symbol, timeframe, trade.EntryTime))`.
  - Clustering runs on `Task.Run`; the render callback is marshalled back to the UI thread via `Dispatcher.UIThread.Post`.
  - `KMeansClusterer` is deterministic (fixed RNG seed 42) so the same trades produce the same clusters on every run.

- Cross-tab navigation (see §21 for the messaging contract):
  - `MainViewModel.Receive(NavigateToTradeMessage)` sets `SelectedNode = _chartNode` (Chart is the 10th tab in `MainWindow.axaml`).
  - `ChartViewModel.Receive(NavigateToTradeMessage)` selects the matching queued dataset (loading if needed) and sets `_pendingNavigationTime`, consumed by `ApplyPendingNavigation` after the series loads. The chart's X-axis is set to a 200-bar window centered on the target timestamp.
  - If the target `Symbol @ Timeframe` is not in the `SelectionQueue`, the Chart view shows a warning — navigation does not mutate the queue.

- Keyboard navigation (all plots in the tab and the Chart tab):
  - `AvaPlot.UserInputProcessor.Disable()` on every plot so pointer drag cannot get stuck mid-pan.
  - Arrow keys pan 10% of the visible span per press. `+` (main `=` or numpad `+`) trims 10% off each end of the X-axis; `-` (main `-` or numpad `-`) grows the window by 10% on each side.
  - Plots are `Focusable="True"`; keys register after a single click to focus.

- Tab position: `MainWindow.axaml` places Monte Carlo between Runner and Metrics.

## 15. Selection Queue

- Purpose: the single source of truth for "which cached datasets are enabled for use" across all data-consuming tabs.
- Core type: `SelectionQueue` (`src/core/SelectionQueue.cs`), a singleton. Holds `ObservableCollection<QueuedDataset>` where each entry is `(Symbol, Timeframe, Provider)`.
- Persistence: written to `AppSettings.QueuedDatasets` as `"symbol@timeframe"` strings in `config/settings.json`. Loaded on `ConfigManager` construction. Restored entries get `Provider = "Unknown"` — the provider is only correct at the moment of queueing.
- API:
  - `Add(symbol, timeframe, provider)` — no-op if the pair is already queued.
  - `Remove(symbol, timeframe)` — no-op if not queued.
  - `Clear()`.
  - `Contains(symbol, timeframe)`.
  - `Snapshot()` — returns a copy for modal use.
  - `Changed` event — raised after every mutation.
- Group-header bulk select: each synthetic group-header row in the Data tab carries a checkbox. Toggling it writes through to `SelectionQueue` for every *visible* row in that group (filter-aware). Header state auto-syncs to the members — checked when all visible rows are queued, unchecked otherwise (two-state, no tri-state). Group headers are never queued themselves; only the member rows are.
- Consumer wiring (`src/gui/Data/`):
  - `DataRowViewModel` wraps each `DataEntry` with an `IsQueued` observable property. Checking the box calls `SelectionQueue.Add`. Unchecking calls `SelectionQueue.Remove`. One write-through per toggle, no batching.
  - `DataViewModel.OpenQueueCommand` raises `OpenQueueRequested`. `DataView.axaml.cs` handles it by opening `QueueWindow` modally.
- Modal behavior (`QueueWindow.axaml` + `QueueViewModel`):
  - Opens with a snapshot of the current queue. Every row starts checked.
  - Unchecking a row marks it `PendingRemoval` but does NOT remove it. Row dims to 45% opacity.
  - `Done` iterates rows and calls `Remove` on every row with `PendingRemoval == true`. Closes with result `true`.
  - `Cancel` closes with result `false` and commits nothing.
  - Window close button (X) behaves like Done — commits pending removals.
  - Two rows cannot be in-flight simultaneously — the modal owns a snapshot, mutations happen only on Done.
- Refresh path: `DataViewModel` ctor fires `RefreshData` fire-and-forget. `DataManager.GetAvailableDataAsync` runs the per-file metadata reads under `Parallel.For` inside a `Task.Run`, so the tab renders immediately with `Scanning cache…` and populates when the scan completes. A `CancellationTokenSource` field gives latest-wins semantics — a fetch-triggered refresh cancels the in-flight ctor-triggered one instead of racing it. The synchronous `GetAvailableData` is kept for callers that already run off the UI thread; new UI-facing code should use the async overload.
- Data tab layout:
  - **Grouped by ticker.** `Rows` carries one synthetic group-header `DataRowViewModel` (`IsGroupHeader = true`, `GroupLabel = "{Symbol}   ({N})"`) followed by that symbol's data rows. Header rows render bold `#4FC3F7` on the Symbol column, blank everywhere else. All cell templates gate on `IsVisible="{Binding !IsGroupHeader}"` so no empty cells appear on a header.
  - **Within-group order** is `TimeframeMap.All` index descending — widest bar first (1W, 1D, 4h, 1h, 15m, … 1m). Tiebreak on `Path`.
  - **Group order** is alphabetical by Symbol; the Symbol header button flips it asc/desc. Other column headers still toggle their glyph, but the grouping layer ignores them.
  - **Filter interaction.** Filters apply per row first, then grouping runs over the survivors. A group with zero surviving rows disappears entirely.
  - Leftmost column: `On` checkbox (`DataGridTemplateColumn` with a `CheckBox` in the cell template).
  - Columns: Symbol, Timeframe, Tier, Provider, Has Vol, Size, Modified, Total Range, Path. All headers support dynamic sorting and filtering via text boxes above the grid.
  - `Total Range` renders `DataRowViewModel.RangeDisplay` (`ProviderDatasetRow.FormatDuration` over `RangeTicks`). `RangeTicks = MaxT - MinT` is `0` when `MinT <= 0`, `MaxT <= 0`, or `MaxT <= MinT`; those rows render an em dash (U+2014) and sort to the bottom in either direction.
  - Sorting by `Total Range` orders member rows inside each ticker group on the numeric `RangeTicks` (`DataViewModel.OrderWithinGroup`), not on the display string. Group order stays alphabetical by Symbol; every other column header reorders the member rows within each ticker group via DataViewModel.OrderWithinGroup (Timeframe, Tier, Provider, HasVolume, SizeBytes, LastModifiedLocal, RangeTicks, Path), ascending or descending per click, falling back to timeframe-descending when SortColumn is Symbol.
  - `FilterRange` in the Row 2 filter strip matches against the rendered `RangeDisplay` text.
  - Toolbar: `Refresh`, `Queue…`, and a live `N queued` counter bound to `QueuedCount`.
  - Status line: `"{Rows.Count} of {_all.Count} dataset(s) shown — {QueuedCount} queued."`
- Empty queue: legal state. Consumers (Runner, Chart) will refuse to run and show a hint.
- Fetch form (`DataView.axaml` Row 0): symbol TextBox, timeframe ComboBox (`TimeframeMap.All`), provider ComboBox, Fetch button, Fetch & Queue button, ProgressBar, status text.
  - Provider ComboBox lists `Auto` plus every key in `AppSettings.ProviderOrder`. `Auto` (default) uses the standard walk. Selecting a specific provider forces a refetch when the cached `source` differs, and the walk becomes `[preferred, ...rest]`. The provider list is snapshotted at `DataViewModel` construction.
  - `FetchCommand` — calls `DataManager.GetSymbolDataAsync(symbol, timeframe, progress, preferred)`, refreshes the row list on success, does not touch the queue.
  - `FetchAndQueueCommand` — same fetch, then writes to `SelectionQueue` with the provider name resolved from the cache metadata of the freshly written file.

## 16. Providers Tab

- Purpose: read-only diagnostics over `data/cache/`, grouped by the `source` field of each cache envelope. Never triggers network fetches — reads `CacheManager.ReadCacheMetadata` directly, bypassing `DataManager` by design.
- GUI (`src/gui/Providers/`):
  - `ProvidersView.axaml` — toolbar (Refresh Stats), scrollable grouped list, status line.
  - `ProvidersView.axaml.cs` — resolves `ProvidersViewModel` via DI in the constructor.
  - `ProvidersViewModel` — `Groups` (ObservableCollection of `ProviderGroupViewModel`), `StatusText`, `RefreshCommand`.
  - `ProviderGroupViewModel` — one per distinct `source`. Holds aggregate `DatasetCount`, `Volume` (`Yes` / `No` / `Mixed (n/m)`), overall `Start` and `End` (min/max across all datasets in the group), `AverageSpan` (arithmetic mean of per-dataset spans, excluding datasets with no valid `MinT`/`MaxT`), `Timeframes` (distinct, sorted by `TimeframeMap.All` order first then lexicographic), `IsExpanded` (default true), `ToggleExpandCommand`, and `Datasets`.
  - `ProviderDatasetRow` — one per cache file. `Dataset` (`symbol@timeframe`), `Volume`, `Start`, `End`, `Span`, `Timeframes` (the single timeframe of this file). Static `FormatSpan` / `FormatDuration` helpers, shared with the group.
- Columns, in order: Provider, Dataset, Volume, Start, End, Avg Span, Timeframes.
- Layout: `ItemsControl` of provider groups. Each group renders a bold provider header row followed by an indented `ItemsControl` of dataset rows, hidden when `IsExpanded` is false. Column alignment relies on identical fixed `ColumnDefinitions` (`220,220,100,110,110,110,*`) in the header grid and the dataset grid. No user column resizing.
- Span formatting (`ProviderDatasetRow.FormatDuration`): `< 1 h` → minutes, `< 1 d` → hours, `< 60 d` → days, `< 24 mo` → months (30.4375 d/mo), else years (365.25 d/y). One decimal place. Datasets with `MinT <= 0`, `MaxT <= 0`, or `MaxT <= MinT` render `—` and are excluded from the group's `AverageSpan`.
- Refresh: manual only, via the toolbar button. No `FileSystemWatcher`. The `ProvidersViewModel` constructor kicks off `RefreshAsync` fire-and-forget, so the tab renders immediately and the grid populates when the scan completes.
- RefreshAsync runs the per-file metadata reads on the thread pool via `Task.Run(() => Parallel.ForEach(files, ...))`. Each `CacheManager.ReadCacheMetadata` call fully parses a cache JSON and enumerates every bar, so a cache with dozens of files was blocking the UI thread for several seconds. The parallel-off-thread pass collapses that to roughly `wall / coreCount` while keeping the window interactive throughout.
- Aggregation and `ObservableCollection<ProviderGroupViewModel>` population stay on the UI thread — that part is O(files) and cheap; it cannot reintroduce the freeze. The status line reads `Aggregated stats for {N} provider(s) across {M} cached dataset(s) in {ms} ms.` and the same timing is logged under `LogTag.Cache`.
- Re-entrancy guard: `_refreshing` flag prevents overlapping refresh invocations.
- `CacheManager.ReadCacheMetadata` contract: single pass over `bars[]`, collecting `HasVolume` and running `MinT`/`MaxT`. Does not assume ascending bar order. `MinT`/`MaxT` remain `0` when `bars` is empty or absent.

## 17. Time-Domain Monte Carlo (Phase 10.15)

Replaces the original trade-shuffling Monte Carlo engine. The trade domain is statistically invalid for prop-firm evaluation because it destroys intra-trade equity paths, ignores trailing drawdown, and treats time as irrelevant.

### Data model
- `TimeSlice` (`src/core/Overview/TimeSlice.cs`) — readonly struct. `Timestamp` (Unix ms), `MtmHigh`, `MtmLow`, `RealizedPnl`, `IsEodReset`. One slice per Kline. `LayoutKind.Sequential`. `double` not `float` — 5000 bars × 1000 paths is small enough that float's precision loss is not worth the marginal speed.
- `TimeSliceBuilder` (`src/core/Overview/TimeSliceBuilder.cs`) — pure static function `Build(IReadOnlyList<Trade>, IReadOnlyList<Kline>) → TimeSlice[]`. Per-slice MTM reflects intra-candle excursion of the open position. RealizedPnl is written only on the trade's exit slice. EOD flag is set on the first slice of each new day (day-of-year transition). Allocation-free: one array, no LINQ, no lists in the hot path.

### Trade record extension (Phase 10.15A)
- `Trade` gains `EntryIndex`, `ExitIndex`, `Size`.
- `TradeRecorder.Enter(side, price, size = 1.0)` — size defaults to 1.0. Existing strategies compile unchanged.
- `PnL` is now size-scaled (`(exit - entry) * size` for long). `PnLPercent` remains per-unit (`(exit - entry) / entry`).

### Trade → TimeSlice relationship
For each trade, every slice between `EntryIndex` and `ExitIndex` carries:
- `MtmHigh = (High - entryPrice) * size` for long, `(entryPrice - Low) * size` for short.
- `MtmLow = (Low - entryPrice) * size` for long, `(entryPrice - High) * size` for short.
- `RealizedPnl = trade.PnL` on the exit slice only, 0 elsewhere.

Trades cannot overlap today — `TradeRecorder._hasOpen` enforces it. If multi-position strategies are added later, the accumulator in `TimeSliceBuilder` sums MTM across simultaneous trades.

### Engine
`MonteCarloEngine` consumes the `TimeSlice[]` and owns the aggregation half of the computation; the per-slice state machine lives in the computing subsystem (§22). The engine is constructed with an `IComputeDispatcher` and dispatches one `MonteCarloDispatchRequest` per run — it does not step through slices itself and owns no parallel loop. The state machine it used to run internally is `SimulationKernelCpu.Simulate` (`src/core/Computing/Backends/SimulationKernelCpu.cs`), the former `MonteCarloEngine.SimulateOnce` moved statement for statement and invoked per iteration through `IComputeBackend`; the same `MonteCarloSimulationRequest` also drives the ComputeSharp GPU kernel (§22). Results come back as `MonteCarloDispatchResult` — nine per-iteration arrays plus the optional equity curves — not as bare terminal balances, and the engine only aggregates them into `MonteCarloResult` (`BuildResult`). What it implements:
- Stationary block bootstrap (Politis & Romano): block length drawn from a geometric distribution with mean `ExpectedBlockSize`. Circular wrap.
- Flat-bet absolute PnL compounding (`CurrentBalance += RealizedPnl`), not percentage. Prop firm capital is nominal and fixed-lot.
- State machine per slice: MTM low check against daily floor and max-DD floor, then realize PnL, then update HWM, then EOD reset.
- `DrawdownType` from the active account: `static`, `trailing-eod`, `trailing-intraday`. The HWM update semantics differ per type.
- Outputs: `P(ruin)`, `P(funded)`, `P(timeout)`, expected time to fund, expected time to ruin, best-day-rule violation probability, streak distributions (losing trade and flat stretch), ruin cause breakdown (daily-floor vs max-DD-floor).
- Target duration from `AccountPhase.TradingPeriodDays` (fallback 90 days).
- Independent Monte Carlo runs per queued dataset. Aggregation sums expectations, does not pool trades.
- Hard limits: `MaxIterations = 100_000`, `MaxCurveCells = 100_000_000` (see §14). Cancellation flows through `IComputeDispatcher.DispatchMonteCarloAsync`; a cancelled dispatch returns the iterations that completed (truncated at a chunk boundary, never padded) rather than throwing.
- When the curve retention ceiling is exceeded, curves are dropped and `MonteCarloResult.CurvesRetained` is `false`; all scalar stats still populate.

## 18. Data Automation

- Purpose: autonomously capture OHLCV screenshots from a running TradingView Desktop window into `data/shots/`, OCR them into `data/parsed/*.json`, and run diagnostics on the parsed output. Exposed as a single tab with three subtabs: **Extraction**, **Parsing**, **Analysis**.
- Location:
  - Core: `src/core/DataAutomation/` — `ExtractionCore.cs` (window detection, input, capture, calibration, orchestrator), `ParsingCore.cs` (OCR parsing pipeline), `BatchedOcrRecognizer.cs` (ONNX Runtime wrapper), `OcrWorkerHost.cs` / `OcrWorkerClient.cs` (out-of-process worker), `AnalysisService.cs` (parsed-file diagnostics).
  - GUI: `src/gui/DataAutomation/DataAutomationView.axaml`, `.axaml.cs`, `DataAutomationViewModel.cs`.
- Tab position: `MainWindow.axaml` places Data Automation between Data and MT5 Ticks.

### Extraction subtab

- Services (all constructed inside `DataAutomationService`; only `DataAutomationService` is DI-registered):
  - `TradingViewWindowDetector` — passive window discovery via `EnumWindows` + `GetWindowThreadProcessId` matched against `Process.GetProcessesByName("TradingView")`. No injection, no hooks.
  - `InputSimulatorService` — `SendInput` P/Invoke. Methods: `FocusAndMaximize`, `ClickAt(x,y)`, `LeftClick`, `PressLeftArrow`, `PressRightArrow`. No keyboard or mouse hooks.
  - `DesktopDuplicator` — screen capture via Win32 `PrintWindow` with `PW_RENDERFULLCONTENT`. Used instead of DXGI Desktop Duplication because TradingView is Chromium-based and returns black frames under DXGI. Returns null when `PrintWindow` refuses.
  - `AutomationCalibrationService` — JSON persistence at `config/data_automation.json`. Stores a `Dictionary<string, AutomationRegion>` keyed by field (DATE, TIME, O, H, L, C, V) plus a single `AutomationPoint` click point.
  - `DataAutomationService` — orchestrator. Exposes `LoadCalibration`, `SaveCalibration`, `ClearCalibration`, `PrepareCalibrationAsync` (frozen snapshot + rect for the overlay), and two `RunAsync` overloads: full extraction `(parameters, progress, ct)` and filtered re-extract `(parameters, stepFilter, progress, ct)`.

- Calibration: **per-field**. Each of DATE, TIME, O, H, L, C, V is captured independently through a modal `CalibrationOverlayWindow` defined in `DataAutomationView.axaml.cs`. The click point is a single point on the chart body. Only O/H/L/C/V are required to enable Start Automation; DATE and TIME are optional — when absent, timestamps are derived by walking back from the newest bar at one timeframe step per captured bar. The overlay shows a frozen `PrintWindow` snapshot of the TradingView window, a dimmed mask on the four strips outside the current selection, and a live green selection border. ESC cancels.

- Mixed-DPI handling: the process is DPI-unaware/SystemAware. The overlay is (1) sized once at creation using the owner's `RenderScaling`, (2) re-fitted on `ScalingChanged` and on `Opened + Dispatcher.Post(Loaded)` using its own `RenderScaling`, and (3) after `ShowDialog` returns, its final `RenderScaling` is the authoritative factor used to convert drag/click coordinates into the physical-pixel space that `PrintWindow` produces and the automation loop consumes.

- Automation loop (`DataAutomationService.RunAsync`):
  1. Focus + maximize the TradingView window.
  2. One `ClickAt` at the calibrated click point (window-relative → absolute screen coordinates) to focus the chart.
  3. **Alignment**: press Left Arrow once, hash the reference region (FNV-1a over the region's raw bytes), wait for the hash to differ from the pre-press hash. Repeats up to `MaxAlignAttempts = 30` with `AlignmentStepDelayMs = 80`. Aborts with a setup error if the reference region never changes.
  4. For each step index `i` up to `maxStep` (full mode: `StepCount - 1`; re-extract mode: `max(stepFilter)`):
     - If `captureThis` (`stepFilter == null || stepFilter.Contains(i)`): `PrintWindow` capture, crop each calibrated region, save as `data/shots/{symbol}_{timeframe}_{i:D4}_{field}.png` (one file per field). Hash the reference crop and use it as the new `currentHash`.
     - Press Left Arrow; poll every 10 ms up to a 5 s hard timeout until the reference-region hash differs from `currentHash`. If the timeout fires, log a warning and continue (subsequent captures may be offset).
     - Report progress.

- Re-extract flow: the Extraction subtab has a "Re-extract Missing" button and a text box that accepts the contents of `data/parsed/{symbol}_{timeframe}_misses.txt` (parsed as whitespace- or comma-separated non-negative ints). The service walks from step 0 to `max(stepFilter)`, captures only the target indices, and overwrites the corresponding PNG crops in place. Alignment and click behavior are identical to a full run, so the previous extraction's chart state must be reproduced before re-extract starts.

- Parameters record:

```csharp
public sealed record AutomationParameters(
    string Symbol, string Timeframe, int StepCount, int DelayMs,
    Dictionary<string, Rectangle> ValueRegions, int ClickX, int ClickY);
```

## 19. MT5 Tick Bridge

- Purpose: stream raw `MqlTick` records out of a running MetaTrader 5 terminal, cross-verify fetched ranges against the terminal's own tick counts, force the terminal to download broker-side tick history into its local `.tkc` cache, and parse tick CSVs into kline cache. Exposed as the **MT5 Ticks** tab with four subtabs: **Extraction**, **Requester**, **.tkc Extract**, **Parsing**.
- Location:
  - EA: `docs/Backs2TickBridge.mq5` (v1.18). Native MQL5, no inputs.
  - Core: `src/core/MT5/` — `Mt5Tick.cs`, `Mt5TickServer.cs`, `Mt5TickService.cs`, `Mt5TickParser.cs`, `Mt5Models.cs`.
  - ViewModel: `src/gui/MT5/Mt5TickViewModel.cs` plus partials `.Extraction.cs`, `.Requester.cs`, `.Parsing.cs`, `.Tkc.cs`, and `RangeObservableCollection.cs`.
  - Writer: `src/core/Writers/Mt5TickWriter.cs` (both `Mt5TickWriter` and `Mt5TickStreamWriter`).
  - **Note:** `Mt5TickViewModel.Requester.cs` currently sits under `src/core/MT5/` on disk even though its namespace is `backs2.ViewModels`. It belongs with the other `Mt5TickViewModel` partials under `src/gui/MT5/`; the physical location is a known inconsistency.

### Transport

- Raw TCP over BCL `System.Net.Sockets`. No ZeroMQ, no DLL imports, no IPC library.
- MQL5 sockets are **client-only**. The EA connects out to a C# `TcpListener` owned by `Mt5TickServer`. MQL5 does not implement `SocketBind`/`SocketListen`/`SocketAccept`.
- Default: `127.0.0.1:5555`. Both sides define this via `#define BRIDGE_HOST` / `#define BRIDGE_PORT` on the EA and `AppSettings.Mt5Host` / `AppSettings.Mt5Port` on the C# side. Change both.
- MT5 must whitelist `127.0.0.1` under **Tools → Options → Expert Advisors → Allow WebRequest for listed URL** — the WebRequest list governs `SocketCreate`/`SocketConnect`. Without it, the EA fails with `SocketConnect FAILED, error 5273`.

### Wire format

Newline-delimited JSON. One request message in, one response per request, matched by a monotonically increasing `id`. The EA echoes `id` on the response. Two unsolicited message types exist and use `id:0`:

- `HEARTBEAT` — idle keepalive every 5 s when no long-running task is active.
- `PROGRESS` — emitted between chunks/days of a long-running operation, carrying the originating request's id under `request_id`.

Requests:

    {"id":1,"action":"PING"}
    {"id":2,"action":"GET_TICKS","symbol":"GOLD.pro","from_msc":1700000000000,"count":20000}
    {"id":3,"action":"GET_TERMINAL_INFO"}
    {"id":4,"action":"COUNT_TICKS","symbol":"GOLD.pro","from_msc":X,"to_msc":Y,"chunk_ms":21600000}
    {"id":5,"action":"SYNC_TICKS_RANGE","symbol":"GOLD.pro","from_msc":X,"to_msc":Y}
    {"id":6,"action":"PREWARM","symbol":"GOLD.pro","from_msc":X,"count":500}

`COUNT_TICKS` without `chunk_ms` runs monolith mode (one `CopyTicksRange` call, one response). With `chunk_ms > 0` it runs chunked mode (one chunk per `OnTimer` tick, `PROGRESS` frames between chunks, adaptive shrink on failure down to `MIN_CHUNK_MS = 60000`).

Responses:

    {"id":1,"status":"OK"}
    {"id":2,"status":"OK","next_from_msc":...,"perf_copy_us":...,"perf_ser_us":...,"ticks":[...]}
    {"id":4,"status":"OK","count":N,"first_msc":...,"last_msc":...,"perf_us":...,"chunks":M}
    {"id":5,"status":"OK","synced_days":S,"skipped_days":K,"total_days":T}

PROGRESS frames (two shapes, same reader):

    {"id":0,"action":"PROGRESS","request_id":4,"chunks_done":D,"chunks_total":T,"cumulative_ticks":C}
    {"id":0,"action":"PROGRESS","request_id":5,"chunks_done":D,"chunks_total":T,"synced_days":S,"skipped_days":K}

### C# side

- `Mt5TickServer` owns the `TcpListener`, accepts one client at a time, splits the incoming byte stream on `\n`, and dispatches by `id`. It exposes:
  - `SendRequestAsync(action, extraJson, timeoutMs, ct)` — write and await by `id`, `TaskCompletionSource<byte[]>` per pending request.
  - `IsClientConnected`, `HasHeartbeat`, `LastHeartbeatAge` — liveness signals.
  - `CountProgress` event — fired for every PROGRESS frame after parsing.
  - `TryParseProgress` — relaxed reader. `chunks_done` and `chunks_total` are required; `cumulative_ticks`, `synced_days`, `skipped_days` are all optional and default to 0. A missing field means "not applicable for this action", not "malformed".
- `Mt5TickService` wraps the server and exposes typed operations:
  - `PingAsync(ct)` → `Mt5PingResult`.
  - `GetTerminalInfoAsync(ct)` → raw JSON string.
  - `PrewarmAsync(symbol, fromMsc, ct)` — idempotent per symbol via `HashSet<string> _warmedSymbols`. Fired automatically before the first `GET_TICKS` per symbol per process.
  - `FetchTicksAsync(symbol, fromMsc, targetTicks, chunkSize, progress, ct, onBatch)` — the tick pull loop. `onBatch` is **awaited inside the loop** as deliberate backpressure so a slow disk writer bounds memory. Never change to fire-and-forget.
  - `CountTicksAsync(symbol, fromMsc, toMsc, progress, ct)` — chunked range count. Timeout = `Mt5TimeoutMs + min(spanDays, 90) × 3000 ms`. Attaches a handler to `_server.CountProgress` for the duration of the request.
  - `SyncTicksRangeAsync(symbol, fromMsc, toMsc, progress, ct)` — broker-side history download. Timeout = `Mt5TimeoutMs + clamp(spanDays × 500 ms, 20 min, 60 min)`. Same `CountProgress` attach/detach pattern.
- `Mt5TickService` exposes the raw `CountProgress` event (add/remove delegates onto `_server.CountProgress`) for callers that need the raw stream, but the preferred path for new code is the `IProgress<Mt5CountProgress>?` parameter on `CountTicksAsync` / `SyncTicksRangeAsync`.
- `Mt5CountProgress` (record) — `RequestId`, `ChunksDone`, `ChunksTotal`, `CumulativeTicks = 0`, `SyncedDays = 0`, `SkippedDays = 0`. One record for both actions; action-specific fields default to 0.

### Inclusive-boundary rule (critical)

`CopyTicks`'s `from_msc` is **inclusive**: it returns ticks with `time_msc >= from_msc`. After a slice that ends with K ticks at time T, the next `CopyTicks(from=T)` re-emits all K ticks at T. Both sides track the trailing same-ms count and skip exactly that many leading ticks on the next slice:

- EA (`HandleGetTicks`): `lastTimeMsc` / `lastTimeTrailing`.
- C# (`Mt5TickService.FetchTicksAsync`): `_lastEmittedTimeMsc` / `_lastEmittedTrailing`.

Never send `next_from_msc = lastMsc + 1`. It silently drops same-ms ticks at every seam. A change on one side must be mirrored on the other.

### Prewarm

Per-symbol per-process. First `CopyTicks` per symbol costs ~2× warm (tick-DB page-in). Dropping prewarm costs ~30% of first-fetch throughput. Idempotent via `HashSet<string> _warmedSymbols` guarded by `_warmedLock`. Do not bypass.

### Serialization

The EA serializes `GET_TICKS` responses into a pre-sized `uchar[]` byte buffer via `AppendStr` + `SocketSend`. Never use MQL5 `string` concat (`+=` or `StringAdd`) on the hot path — both reallocate on every append and are measured O(N²). 20,000 ticks took 39.9 s with string concat in v1.08; the byte-buffer path does the same in ~14 ms. `MAX_REQUEST_TICKS = 50000` is the EA-side batching ceiling per request. `COPYTICKS_CAP = 2000` is the hard per-`CopyTicks`-call cap. Do not raise either without measuring.

### SYNC_TICKS_RANGE (v1.17+)

- Forces the terminal to download a tick range from the broker into its local `.tkc` cache. No raw ticks cross the wire — the data lands on disk in MT5's own monthly cache format.
- EA-side loop: iterates one day (`ONE_DAY_MS = 86400000`) per `OnTimer` tick using `CopyTicksRange(COPY_TICKS_ALL)`. On `ERR_HISTORY_TIMEOUT (4403)` with zero ticks, retries the same day on the next 10 ms tick, up to `SYNC_PER_DAY_TIMEOUT_MS = 30000`. Any non-timeout return — including a successful zero-tick response — advances the cursor.
- **Immediate ack is mandatory.** `HandleSyncTicksRange` emits an initial PROGRESS frame the moment the request is accepted, throttle bypassed (`g_sync_last_prog_ms = 0`). The first `CopyTicksRange` on a cold symbol can block for minutes while the terminal downloads history; without an immediate ack, the C# side has no confirmation the request landed and will time out on a live request. Do not remove this ack.
- **C# timeout must be span-scaled.** `SyncTicksRangeAsync` computes `Mt5TimeoutMs + clamp(spanDays × 500 ms, 20 min, 60 min)`. A 10-year sync gets ~30 minutes. Do not reuse the COUNT_TICKS formula and do not send `Mt5TimeoutMs` alone.
- Latest-wins: a new SYNC or COUNT_TICKS request pre-empts any in-flight one on the EA. No multiplexing.

### Extraction subtab

- `FetchTicksCommand` streams ticks to `data/ticks/{symbol}_{stamp}.csv` via `Mt5TickStreamWriter` on a bounded `Channel<IReadOnlyList<Mt5Tick>>` (64-batch depth), flushed by a background task. The `onBatch` callback writes into the channel; a slow disk blocks the socket read rather than buffering in RAM.
- Grid preview is capped at `Mt5TickViewModel.MaxGridRows = 100_000`. `PopulateGrid` auto-unchecks once the cap is hit; `OnTargetTicksChanged` auto-unchecks when `TargetTicks > 500_000`. Never raise the cap without re-validating the DataGrid freeze behavior — the freeze was a `Reset`-per-batch storm, not a memory issue.
- `RangeObservableCollection<T>.AddRange` batches N per-item `CollectionChanged` events into one `Reset` notification.

### Verify Range panel

- `VerifyRangeCommand` calls `CountTicksAsync` over a UTC-offset-anchored range and compares the MT5 count against the last fetch's `_lastFetchCount`. Renders `✓ MATCH` or `✗ MISMATCH` with a signed delta.
- `ApplyToFetchCommand` copies the verified range back into the fetch parameters: lookback derived from the range start, target sized to the verified count + 1% headroom (capped at 50M). `PopulateGrid` is switched off above 500k.

### Requester subtab

- `RequestSyncCommand` in `Mt5TickViewModel.Requester.cs` drives `Mt5TickService.SyncTicksRangeAsync`.
- Progress arrives via `IProgress<Mt5CountProgress>` built on the UI thread, so `Progress<T>` marshals every `Report` back to the UI dispatcher — the `ReqProgress` and `ReqStatusText` `[ObservableProperty]` setters stay on the right thread.
- Status line: `Syncing {symbol} — day D/T (P%) | synced S, skipped K`.

### CSV → kline cache parsing

- The Parsing subtab runs the CSV → bars aggregation **inside `Task.Run`**. Never inline on the UI thread.
- Bars are built from `Bid`, `Volume` = tick count. Never use `Ask` or `Last` — forex/metals `Last` and `Volume` are empty.
- Zero-bid ticks (`bid <= 0`) are dropped and counted.
- Bucketing is epoch-relative: `(timeMsc / tfMs) * tfMs`. Never re-introduce per-hour anchoring — it breaks 45m.
- Symbol comes from the CSV filename before the first `_`. Never parse it from a column.
- Cache source is **`MT5 Parsed`**, written through `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`. `MT5 Parsed` is cache-only — never fetched over the network.

## 20. MT5 .tkc Cache Extraction

- Purpose: read MetaTrader's own monthly tick-cache files directly from disk, decode them without a bridge, aggregate to kline bars, and write them into the standard kline cache with source `MT5 Parsed`. No EA, no TCP — pure file I/O.
- Location: `src/core/MT5/tck/`:
  - `TckModels.cs` — `TkcTick(long TimestampMs, double Bid, double Ask)` (readonly record struct), `TkcInfo(uint Magic, string Symbol, long FileSize, int TickCount, long? StartMs, long? EndMs)`, `TkcException`.
  - `TkcDecoder.cs` — static. `Decode(string path)` → `(TkcInfo, IReadOnlyList<TkcTick>)`.
  - `TkcFile.cs` — lazy wrapper (`Info`, `Ticks`, `Symbol`, `Count`) that decodes once on first access. Not thread-safe.
  - `TkcFile.cs` also hosts `TkcRepository.ListSymbolFiles(terminalDataRoot, broker, symbol)` — enumerates `{root}\bases\{broker}\ticks\{symbol}\YYYYMM.tkc`, sorted ordinal-ignore-case.
- GUI: `.tkc Extract` subtab in `src/gui/MT5/Mt5TickViewModel.Tkc.cs`. Drives `ExtractToCacheCommand`.

### File format

- Magic `0x000001FD` at offset 0.
- Symbol as UTF-16LE in `[0xA0, 0xD0)`, terminated by NUL.
- Index starts at `0x00E4`, 48 bytes per entry, terminated by `size == 0`, non-monotonic `offset`, or `offset + size` running past EOF. Max 4096 entries.
- Day payload is a sequence of interleaved zlib streams, each beginning with `0x78` and one of `0x01 | 0x5E | 0x9C | 0xDA`.
- Per-record layout is 16 bytes: timestamps are delta-encoded (first record absolute Unix-ms, subsequent `u64` deltas); bid/ask are `f64` in the first **or** second half of the record, detected per block by `DetectPriceOffset`. Never hard-code the offset.
- A day's blocks are grouped `[Timestamps, Prices, Prices, ...]`. `DecodeFromBlocks` walks forward and decodes each group independently — never pick the single largest `Timestamps` block globally.
- `InflateZlib` is given the full remaining `[offset..offset+size)`; `ZLibStream` self-terminates at the zlib footer. Never cap the input slice.
- `ClassifyBlock` checks for a `Timestamps` block **first** (first 8 bytes decode as Unix-ms in `[1.42e12, 2.0e12]`). Do not check zero-fraction first — delta-encoded timestamps can be 0 for many consecutive ticks, which previously caused valid timestamp blocks to be tagged `Empty`.

### Decode strategy

- **Serial across files**, never parallel. The full-history GOLD.pro set holds ~3.6 GB of ticks in RAM if decoded in parallel. Peak memory is bounded by the largest single file's tick list plus the bucket dict.
- Iterate files **newest-first** and ticks within a file **in reverse**. This lets `TkcLookbackDays` and `TkcMaxTicks` stop the decode mid-file without materialising every tick.
- Bucket semantics under reverse iteration: **first** bid seen in a bucket is **Close**, **last** bid seen is **Open**. High/Low are symmetric.
- `FileOverlapsLookback` cheap-parses `YYYYMM.tkc` filenames to skip whole months before the cutoff. Non-standard filenames are conservatively included.
- Bars are built from `Bid`, `Volume` = tick count. Zero-bid ticks (`Bid <= 0`) are dropped and counted.
- Timeframe bucketing is epoch-relative: `(timeMsc / tfMs) * tfMs`. Same rule as the CSV parser. `1D` = UTC midnight, `1W` = ISO Monday UTC.
- Cache source is **`MT5 Parsed`**, written through `CacheManager.SaveCacheAsync("{symbol}@{timeframe}", klines, "MT5 Parsed")`.

### Logging

- Exactly **one** `Info` line per file: `TKC '{fileName}': {tickCount:N0} ticks, {indexCount} day(s), {elapsed:N0} ms`.
- At most **one** `Warn` per run: the zero-bid drop count.
- Never add per-day, per-block, or per-stream logging to the decoder. The last time this rule was violated, 60 lines/file of diagnostics buried a silent-tick-loss bug for hours.

### Constraint

- Never run `.tkc` ticks directly through a strategy or metric. `RunnerScriptGlobals.Data` is `List<Kline>`, `IMetric.Calculate` takes `List<Kline>`, `TimeSliceBuilder` produces one slice per Kline, and `CacheManager` stores `KlineCompact[]`. Always aggregate to bars first — running on raw ticks would need a parallel execution engine and would silently break bar-based indicators.

## 21. Compute Worker (Phase 1)

- Purpose: run heavy compute (future: strategy execution, Monte Carlo) in a separate OS process so the UI survives a compute crash, and so a memory cap can be enforced independently of the UI process. Phase 1 delivers the infrastructure only — the worker runs a placeholder job. Phase 2 migrates `StrategyManager`, Phase 3 migrates `MonteCarloEngine`.
- Location: `src/core/Compute/`:
  - `JobConfig.cs` — `record JobConfig(string ScriptPath, string CachePath, IReadOnlyDictionary<string,string> Params, int MemoryLimitMb)`.
  - `JobProgress.cs` — `record JobProgress(string Phase, int SlicesElapsed, int TotalSlices, double PercentComplete)`.
  - `WorkerEnvelope.cs` — single message envelope with kind-tagged nullable payload slots. Kinds: `Start`, `Progress`, `Result`, `Error`, `Cancel`, `Ping`, `Pong` (constants in `WorkerMessageKind`). Also carries `WorkerResult(int TradeCount, string Summary)` — Phase 1 placeholder.
  - `WorkerIpc.cs` — length-prefixed JSON frames over any `Stream`. 4-byte little-endian length prefix, `JsonSerializer` payload with camel-case and `IgnoreCondition = WhenWritingNull`. Hard ceiling `MaxFrameBytes = 64 MB`.
  - `JobObjectHandle.cs` — Win32 Job Object wrapper. `TryAttach(processHandle, memoryLimitMb, out error)` creates the job on first call, sets `JOB_OBJECT_LIMIT_PROCESS_MEMORY` via `SetInformationJobObject(JobObjectExtendedLimitInformation)`, then `AssignProcessToJobObject`. No-op on non-Windows (returns false with `"Job Objects are Windows-only"`). Disposal closes the handle, which terminates any surviving child process.
  - `WorkerHost.cs` — worker-side main loop. Parses `--in=... --out=...` args, opens `AnonymousPipeClientStream` streams, then reads envelopes: responds to `Ping` with `Pong`, returns on `Cancel`, runs the placeholder job on `Start` (5 progress frames over ~1s + a result). All diagnostics go to `Console.Error` (captured by the parent as `[worker <pid> stderr]` Debug lines).
  - `ComputeWorkerManager.cs` — UI-side singleton. `RunJobAsync(config, progress, ct)` spawns the worker process, wires two anonymous pipes, assigns the child to a Job Object, sends `Start`, pumps envelopes until `Result` (returned) or `Error` (thrown). Best-effort `Cancel` message on cancellation, then unconditionally `Kill(entireProcessTree: true)`. Captures worker stderr/stdout into the parent log at Debug level.
- Process model: the worker is the same `backs2.exe` binary invoked with `--worker`. `Program.Main` dispatches `--worker` and `--ocr-worker` before touching Avalonia or `LogManager` — the worker does not initialize Avalonia, does not open the log file, and does not touch the DI container. `ComputeWorkerManager.ResolveHost` handles both `dotnet <entry>.dll` and self-contained deployments.
- Wire format: one `WorkerEnvelope` per frame, JSON, newline-delimited at the frame boundary (length-prefix, not newline). Pipe directions from the parent: `serverOut` (parent writes / worker reads) and `serverIn` (parent reads / worker writes). The client handles passed to the worker must match the worker's own direction — `--in` receives the client handle of `serverOut`, `--out` receives the client handle of `serverIn`.
- Dispatch from `Program.Main`:
  ```
  --ocr-worker       → OcrWorkerHost.RunAsync()
  --worker           → WorkerHost.RunAsync(args)
  --compute-selftest → RunComputeSelfTest(args)   ; CLI smoke test
  (none)             → Avalonia app
  ```
- UI-side smoke test: the Monte Carlo settings expander has a **Test Worker** button bound to `TestWorkerCommand`. It constructs a `JobConfig` with `MemoryLimitMb = 512` and awaits `ComputeWorkerManager.RunJobAsync`, streaming progress into the status line. Expected log lines on success: `Compute worker started (pid N, mem cap 512 MB)`, `Compute worker pid N: job complete — trades=0, "placeholder job complete"`. Intended to be removed when Phase 3 lands and `MonteCarloViewModel.RunAsync` routes through the worker.

## 22. Computing Subsystem (Phase 28)

- Purpose: single domain-typed entry point for hardware-bound compute in the parent process. Callers request a Monte Carlo dispatch or a backtest batch; they never name a device, a backend, or an IPC frame. Phase 28A shipped the facade and the backend seam, Phase 28B replaced the pass-through with a chunked work-stealing scheduler, and Phase 29B shipped the real DirectX 12 Monte Carlo kernel and retired the null-object placeholder. The move into the worker process is still a future Compute Worker phase. This is the abstraction layer in front of, not a replacement for, the OS-level worker boundary in §21: `src/core/Compute/` is still the only thing that owns a child process, a pipe, and a Job Object.
- Location: `src/core/Computing/` (namespace `backs2.Core.Computing`, backends in `backs2.Core.Computing.Backends`), sibling to `src/core/Compute/`.

### IComputeBackend (`src/core/Computing/Backends/IComputeBackend.cs`)

The contract every backend implements. Two methods, both returning a `MonteCarloDispatchResult` — the nine per-iteration arrays plus optional equity curves — indexed by iteration:
- `Task<MonteCarloDispatchResult> RunSimulationsAsync(TimeSlice[] slices, MonteCarloSimulationRequest request, int iterations, int? seed, bool retainCurves, int maxDegreeOfParallelism, CancellationToken ct)` — whole batch, for a caller that owns the parallelism.
- `Task<MonteCarloDispatchResult> RunIterationsAsync(TimeSlice[] slices, MonteCarloSimulationRequest request, int startIteration, int iterationCount, int? seed, bool retainCurves, CancellationToken ct)` — one contiguous chunk of a batch, added by Phase 28B so the dispatcher can schedule chunks generically. The scheduler owns chunking, work-stealing, and result placement; the backend owns how an iteration range executes. `startIteration` is the absolute batch index.
- Phase 29B widened the return shape from `double[]` (terminal balances only) to `MonteCarloDispatchResult` (`TerminalBalances`, `Outcomes`, `SlicesElapsed`, `MaxDrawdowns`, `BestDayRatios`, `LongestLosingStreaks`, `LongestFlatStretches`, `RuinCauses`, `EquityCurves`), because `MonteCarloEngine.BuildResult` aggregates all nine arrays and the retained-curve path needs the curves. `Count` reports how many iterations a result actually carries, `CurvesRetained` whether every iteration produced a curve, and `Concat` stitches per-chunk results in ascending chunk order. `MonteCarloSimulationRequest` carries `TargetDurationSlices`, `StartingCapital`, the pre-scaled `DailyFloorLoss` / `MaxFloorLoss` / `ProfitTarget` money amounts, `ExpectedBlockSize` and the two trailing-drawdown flags — the kernel never sees a percentage, and the GPU consumes the struct as a uniform constant buffer, so field order and types are part of the ABI.
- `Name` (shown in logs) and `IsAccelerated` complete the surface. Contract: degenerate input (`null`/empty slices, non-positive `TargetDurationSlices`, non-positive iteration count) returns an empty result instead of throwing, and a mid-chunk device failure falls back rather than propagating; cancellation is observed between iterations, never mid-iteration; the same `(slices, request, iterations, seed)` is reproducible **within a backend**. The two backends do not share an RNG implementation (`System.Random` vs the GPU's Philox-4x32), so reproducibility is within a backend; across backends the aggregates are statistically equivalent, not bit-identical. The RNG for iteration `i` is always derived from the **absolute** batch index, never a chunk-local one, so chunk boundaries never move a result.

### ISimulationKernel (`src/core/Computing/Backends/ISimulationKernel.cs`)

- `MonteCarloIterationOutcome Simulate(TimeSlice[] slices, MonteCarloSimulationRequest request, Random rng, int iteration, double[]? curve)` — the per-iteration kernel seam. Keeps the parallel skeleton separate from the Monte Carlo state machine: implementations must be pure, safe to call concurrently, and deterministic for a given `(rng, iteration)`, which is why the RNG is injected per iteration rather than captured. `MonteCarloIterationOutcome` is the full per-iteration result — terminal balance, outcome, slices elapsed, max drawdown, best-day ratio, longest losing streak, longest flat stretch and ruin cause.
- Implemented by `SimulationKernelCpu` (the `SimulateOnce` that shipped through Phase 29A, moved here statement for statement) and by the GPU path in `SimulationKernelGpu`. Both are stateless, so `App.axaml.cs` registers one shared CPU instance and a fresh RNG per iteration is the only per-call state; the null-object `PlaceholderKernel` is gone.

### CpuBackend (`src/core/Computing/Backends/CpuBackend.cs`)

- `RunSimulationsAsync` — `Parallel.For` at `Environment.ProcessorCount`, with a single-iteration fast path that runs inline (ThreadPool dispatch costs more than the work).
- `RunIterationsAsync` — a plain sequential loop on the calling worker thread. Deliberate: the dispatcher already saturates the machine, so a nested `Parallel.For` here would over-subscribe the cores. The scheduler owns the parallelism; this method must not add any.
- RNGs are split as `new Random(seed + i)` on the **absolute** iteration index (`Random.Shared` when `seed` is null). On the chunk-level path the same absolute index seeds the RNG and is passed to the kernel as `iteration`, which is what makes a chunked run identical to the whole-batch call.
- Both entry points run `SimulationKernelCpu.Simulate` per iteration and accumulate the nine result arrays (plus a curve per iteration when `retainCurves` is set) into a `MonteCarloDispatchResult`. Phase 29B also added the one-time `GpuTimeSlice[]` mirroring helper, a plain 32-bit mirror of `TimeSlice` (`Timestamp` int + the three doubles + the EOD byte, no `long`, no padding) that the GPU path feeds to the shader. `TimeSlice` itself is untouched — it is a persistence format.

### GpuBackend (`src/core/Computing/Backends/GpuBackend.cs`)

- ComputeSharp over the DirectX 12 backend, with a real `[Shader]` kernel since Phase 29B. The constructor probes `GraphicsDevice.GetDefault()`, never throws, and creates `SimulationKernelGpu` only when the device reports hardware acceleration *and* kernel creation succeeds; any failure — no device, a software adapter, a kernel-creation failure — logs one `Warn` and leaves the `CpuBackend` fallback carrying every call, with no re-probe and no per-call warning noise. A missing device degrades the feature, never the process.
- `IsAccelerated` is `true` exactly when that kernel exists, so the claim always matches what dispatch does. `Name` reports `GPU (<adapter>)` once a device is held, otherwise the fallback name.
- Dispatch is in process and in chunks of `GpuIterationBatch = 10_000`, matching the scheduler's `McChunkSize`: one chunk is one device dispatch, the slice array is uploaded once per call and reused across dispatches, and the kernel writes one thread's result per simulation. A device chunk that fails re-runs on `CpuBackend` for the same absolute iteration range, so one bad chunk degrades throughput and never the result. Cancellation is observed between dispatches.
- The RNG is **Philox-4x32-10 seeded per thread from `(seed, absolute iteration)`**, not `System.Random`: a shader cannot host `System.Random`, so the two backends are not bit-identical. The 64-bit Philox products are built from 16-bit partial products and the state is plain `uint` scalars, because ComputeSharp 3.2.0 exposes no HLSL vector types to shader code. Group size is 64 threads; the kernel mirrors `TimeSlice` into `GpuTimeSlice` because the struct's `long`/`byte`/padding layout cannot cross into a shader buffer.
- What ships is **correctness, not a win on the current optimizer workload**: the coarse MC pass issues one small dispatch per version, so the fixed per-dispatch cost is not amortized. See the note at the end of this section.

### IComputeDispatcher / CpuComputeDispatcher (`src/core/Computing/`)

- `IComputeDispatcher` is the only compute entry point the rest of the app sees: `Task<MonteCarloDispatchResult> DispatchMonteCarloAsync(MonteCarloDispatchRequest request, CancellationToken ct)` and `Task<double[]> DispatchBacktestBatchAsync(int jobCount, CancellationToken ct)`. `MonteCarloDispatchRequest(MonteCarloSimulationRequest Simulation, TimeSlice[] Slices, int Iterations, int? Seed = null, bool RetainCurves = false, int MaxDegreeOfParallelism = -1)` is a record so the same shape can cross the worker IPC boundary as JSON without reshaping the interface. `RetainCurves` is the difference between a 43 MB output and a few hundred kilobytes on the Phase 29A workload; `MaxDegreeOfParallelism` is 1 for a caller that already saturates the machine from outside, N for an exact thread count, and any non-positive value for `Environment.ProcessorCount`.
- `CpuComputeDispatcher` is the parent-process implementation and the Phase 28B scheduler:
  - `DispatchMonteCarloAsync` chunks `request.Iterations` into a private `McChunkSize = 10_000` and enqueues the chunk indices into a `ConcurrentQueue<int>`. Workers claim work with `TryDequeue` and run until the queue is dry; the pool is `Parallel.For(0, Math.Min(Environment.ProcessorCount, chunkCount), …)`, and a single-chunk batch runs exactly one worker so small dispatches never pay ThreadPool overhead.
  - Each claimed chunk is one `IComputeBackend.RunIterationsAsync` call — the loop is backend-agnostic, so no caller-side branch decides CPU vs GPU and chunks route to `GpuBackend` once `IsAccelerated` flips.
  - `DispatchBacktestBatchAsync` does not enter the queue: one parameter combination is one unit of work, run with `Parallel.For` at `Environment.ProcessorCount`. Backtests are Roslyn executions, non-vectorizable and non-chunkable, so the batch routes to the CPU path only. Phase 29C supplies the per-combination body behind this loop.
  - **Determinism contract.** The per-iteration RNG for iteration `i` is `new Random(seed + i)` on the absolute index in `[0, iterations)`, never the chunk-local index, so a chunked CPU dispatch is identical to `CpuBackend.RunSimulationsAsync(slices, request, iterations, seed, …)` and stays stable when `McChunkSize` or the core count changes. `seed == null` uses `Random.Shared`. The GPU backend satisfies the same contract from the other side: its Philox stream is seeded from `(seed, absolute iteration)`, so a chunk boundary never moves a result even though the two backends' streams differ.
  - **Cancellation.** A cancelled dispatch returns the chunks that completed as a truncated `MonteCarloDispatchResult` and never throws. A chunk is published only once it is demonstrably whole (`chunkResults.Length == iterationCount`), so a cancelled result is a sum of complete chunks rather than an arbitrary iteration cut. Degenerate input returns an empty array.
  - Chunk scheduling and per-chunk completion log at Debug under `LogTag.MonteCarlo`, with the absolute iteration range. No new tag.

### DI wiring

- `App.axaml.cs` registers `SimulationKernelCpu` (shared, stateless), `CpuBackend`, `GpuBackend`, both backends as `IComputeBackend` registrations, and `IComputeDispatcher` -> `CpuComputeDispatcher`, all as singletons.
- `GpuBackend` **is** registered in the parent process as of Phase 29B. The parent holds the DirectX 12 device because the Monte Carlo workload runs in process; `CpuComputeDispatcher` scans the registered backends and resolves the accelerated one, so CPU-vs-GPU selection lives in the dispatcher rather than at every call site. A machine without a device resolves `CpuBackend` and behaves exactly as it did through Phase 28B.
- Forward pointer: only the worker-process migration of the scheduler and the device remains, and it is wiring behind `IComputeDispatcher` and `IComputeBackend` — the loop, the chunk size, and the backend seam do not move.

### Architectural note: GPU dispatch pattern (Phase 29B finding)

- The GPU backend exists and is correct; the coarse-MC dispatch pattern is per-version and therefore does not amortize fixed per-dispatch cost. Batching (Phase 29B.1) is required for the GPU path to win on the current optimizer workload. Measured on the Phase 29A workload (`EtWindow` on `GOLD.pro@30m`, 240 versions that run MC, 20 cores, RTX 4060 Laptop): the kernel itself is 10-16x faster per iteration (100,000 iterations: 480-519 ms GPU vs 6,392-6,714 ms CPU), but the coarse pass (240 x 100 iterations) loses ~5x to the CPU (2,307-2,361 ms vs 261-487 ms warm) because only 4.4% of the phase is kernel work and 95.6% is the slice mirroring, upload, readback and nine output-buffer allocations that each tiny dispatch pays. The refine pass (20 x 1,000 iterations) already flips to a GPU win (89-109 ms vs 112-321 ms). The verdict is about dispatch shape, not kernel capability.

### Cross-tab navigation message

- `NavigateToTradeMessage` (`src/gui/Messages/NavigateToTradeMessage.cs`) — `record(string Symbol, string Timeframe, DateTime TargetTime)`. Broadcast via `WeakReferenceMessenger.Default` (CommunityToolkit.Mvvm). No static, no DI registration — the messenger singleton is the transport.
- Publisher: `MonteCarloViewModel.Cluster.cs` (see §14). Receiver wiring:
  - `MainViewModel` implements `IRecipient<NavigateToTradeMessage>` and sets `SelectedNode = _chartNode` in `Receive`.
  - `ChartViewModel` implements `IRecipient<NavigateToTradeMessage>` and handles dataset select + load + scroll in `Receive`. `_pendingNavigationTime` is consumed by `ApplyPendingNavigation` after the series loads.
- Both receivers call `WeakReferenceMessenger.Default.Register(this)` in their constructors. The parameterless overload requires `IRecipient<T>` on the type.
- The queue is not mutated from navigation. A request for an unqueued `Symbol @ Timeframe` surfaces as a status-line warning in the Chart tab.