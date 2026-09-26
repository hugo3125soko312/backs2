<!-- docs\ARCHITECTURE.md -->
# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: Microsoft.Extensions.DependencyInjection, managed in App.axaml.cs.
- Service Lifecycle:
    - Singletons: LogManager, ConfigManager, DataManager, StrategyManager, StrategyCatalogService, PresetService, MetricRegistry, CacheManager.
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
- Secrets: API keys are read from .env at startup and exposed as read-only ConfigManager properties (TwelveDataApiKey, TiingoApiKey, MassiveApiKey, YahooApiKey). Secrets are never persisted to settings.json.
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
- Provider Order: AppSettings.ProviderOrder is an ordered list of provider keys (default: ["Twelve Data", "Tiingo", "Yahoo", "Massive"]). Edited via the Config tab. Consumed by DataManager.
- Timeframes: Canonical list lives in TimeframeMap.All: 1m, 2m, 3m, 5m, 15m, 30m, 1h, 2h, 3h, 4h, 1D, 1W.

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
- Timeframe resolution is centralized in TimeframeMap:
    - ToTwelveData(tf) -> 1min, 5min, 15min, 30min, 45min, 1h, 2h, 4h, 1day, 1week.
    - ToTiingo(tf) -> 1min, 2min, 3min, 5min, 15min, 30min, 45min, 1hour, 2hour, 3hour, 4hour, daily, weekly.
    - ToMassive(tf) -> (multiplier, timespan).
    - ToYahoo(tf) -> interval string or null.
    - LookbackDays(tf) — per-timeframe fetch window.
- Twelve Data provider: GET https://api.twelvedata.com/time_series?symbol={symbol}&interval={interval}&outputsize=5000&apikey={key}
- Tiingo provider: GET https://api.tiingo.com/iex/{symbol}/prices (intraday) and GET https://api.tiingo.com/tiingo/daily/{symbol}/prices (EOD).
- Massive provider: GET https://api.massive.com/v2/aggs/ticker/{ticker}/range/{multiplier}/{timespan}/{from}/{to}
- Yahoo provider: GET https://query1.finance.yahoo.com/v8/finance/chart/{symbol}
- Secrets: providers read keys from ConfigManager read-only properties sourced from .env — never from AppSettings.

## 7. Caching
- Disk format: KlineCompact record — flat {T, O, H, L, C, V} with T as Unix milliseconds. Serialized via source-generated KlineJsonContext.
- Legacy-format detection: LoadCacheAsync returns null on empty files, malformed JSON, or all-zero payloads, so DataManager refetches.
- Per-file log lines: "Cache: saved {key} ({n} bars, {ms} ms)" and "Cache: loaded {key} ({n} bars, {ms} ms)".
- Memory-tier observability: DataManager logs "RAM hit" / "RAM miss" for every lookup.
- Benchmark: CacheManager.BenchmarkAsync(int barCount) serializes and loads N synthetic bars, logging elapsed milliseconds.

## 8. GUI (MVVM Toolkit)
- Dependency Injection: ViewModels resolved in the View constructor.
- Source Generation: All ViewModels use CommunityToolkit.Mvvm attributes ([ObservableProperty], [RelayCommand]).
- XAML Bindings: x:DataType on every View/UserControl root.
- DataGrid use: Avalonia.Controls.DataGrid 11.0.11.
- ColorPicker use: Avalonia.Controls.ColorPicker 11.0.11 with `ColorSpectrumShape="Ring"`.
- Selectable text policy: TextBlock for clickable list rows; read-only TextBox for source preview, log output, status line.
- RelativeSource binding inside DataTemplates: use `{Binding DataContext.Prop, RelativeSource={RelativeSource AncestorType=UserControl}}`. Do not use `$parent[UserControl].((vm:Type)DataContext).Prop`.

## 9. Tabs
- Runner — strategy catalog, parameter editor, source preview, preset save/load, run button.
- Metrics — metric catalog browser.
- Data — Access-style datasheet with column filters and sort indicators.
- Chart — ScottPlot.Avalonia 5.0.55 candlestick + volume via secondary axis.
- Logs — event-driven live log view.
- Config — auto-generated settings editor, dynamic provider discovery and ordering list.
- (Removed: Strategies tab. Replaced by Runner. Overview tab planned next.)

## 10. Logging

- LogManager is a **static class** in `src/core/LogManager.cs`. It is the sole static in the codebase, and the only exception to the "no static globals" rule — it must initialize before the DI container exists.
- Bootstrap: `Program.Main` calls `LogManager.Initialize()` before `BuildAvaloniaApp().StartWithClassicDesktopLifetime(...)`. The log file is open and writable before any Avalonia XAML loads.
- Crash safety: the entire `Main` body is wrapped in try/catch. Any exception at process level is written to the log with the `[App]` tag, then rethrown so the process exits non-zero.
- Tag enum `LogTag`: `App`, `Cache`, `Data`, `Provider`, `Strategy`, `Runner`, `Account`, `MonteCarlo`, `Overview`, `Chart`, `Config`, `Metric`. Providers use a single `Provider` tag; the provider name is embedded in the message.
- Severity levels:
  - `Info` — main flow. Service started, strategy executed, cache saved, account activated, provider returned data.
  - `Debug` — troubleshooting. RAM hit/miss, per-attempt traces, compile cache hits, firm expansion counts, provider discovery.
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
- RunnerViewModel (src/gui/Runner/RunnerViewModel.cs):
  - Left column: strategy list (from StrategyCatalogService.Current, live-updated via Changed event).
  - Middle column: source preview (lazy-loaded via ReadSourceAsync on strategy selection).
  - Right column: parameter panel, populated by StrategyParameterParser.Parse(source). Scrollable vertically when overflowing.
  - Toolbar: symbol picker (ComboBox + free-text), Refresh Catalog, Run, Presets…, Save Preset….
  - RunAsync: reads current parameter values into a Dictionary<string,string>, fetches data via DataManager.GetSymbolDataAsync(symbol, "1D", IProgress<DataProgress>), invokes StrategyManager.RunBacktestFromFileAsync(path, data, params), raises RunCompleted with the StrategyRunResult.
- RunnerParameterViewModel: wraps a StrategyParameter with mutable CurrentValue / CurrentBool. Exposes HasOptions, IsTextInput, IsBoolToggle, IsDirty. IsDirty is true when EffectiveValue differs from DefaultValue.
- StrategyParameterParser (src/core/Runner/StrategyParameterParser.cs): parses `// @param` lines. Stops at the first non-comment, non-blank line. Supports int, double, bool, string; string params may declare `options: "A", "B"` for a ComboBox.
- TradeRecorder (src/core/Runner/TradeRecorder.cs): records closed trades. Strategy calls Advance(index), Enter(side, price), Exit(price). Timestamps come from Data[index].
- PresetService (src/core/Runner/PresetService.cs):
  - Preset files live in config/presets/{StrategyName}_{PresetName}.preset.json.
  - Filename parsing splits on the first underscore: everything before is the strategy name, everything after is the preset name.
  - Sanitize replaces `_` and invalid filename chars with `-` in the filename; the JSON payload retains the original name.
  - ListForStrategy(name) / ListAll() / Load(path) / Save(name, preset, values) / Delete(path) / GetNextSequentialName(name).
- PresetPickerWindow (src/gui/Runner/PresetPickerWindow.axaml): modal Window with a DataGrid of PresetDescriptor rows, a "Show all strategies" checkbox, Load / Delete / Cancel buttons. Returns the loaded Preset via ShowDialog<Preset?>.
- Save flow: toolbar "Save Preset…" or per-parameter "Save" button (visible only when IsDirty) → inline name prompt (default = GetNextSequentialName) → PresetService.Save.
- Load flow: toolbar "Presets…" → PresetPickerWindow → ApplyPreset writes every value back into the parameter panel.

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

- Purpose: hand off the most recent strategy run's trades and source data to any consumer (Overview tab, Monte Carlo engine, future analytics).
- `OverviewData` record (`src/core/Overview/OverviewData.cs`):
  - `StrategyName`, `StrategyPath` — from the selected `StrategyDescriptor`.
  - `Symbol`, `Timeframe` — the run context (`Timeframe` currently hardcoded `"1D"` until Phase 10.7C ships timeframe selection).
  - `RunUtc`, `Duration` — from `StrategyRunResult`.
  - `Trades` — `IReadOnlyList<Trade>` from `StrategyRunResult.Trades`.
  - `SourceData` — the `List<Kline>` fetched for the run. Required by Monte Carlo for timestamp reconstruction after shuffling.
  - `Error` — null on success, exception message on failure. Failed runs still publish so consumers can display the error state.
- `LastRunStore` singleton (`src/core/Overview/LastRunStore.cs`):
  - `Current` — most recently published `OverviewData`, or null.
  - `Publish(data)` — replaces `Current`, raises `Changed`.
  - `Changed` — event raised after every publish. Consumers subscribe and marshal to the UI thread with `Dispatcher.UIThread.Post`.
  - Lock-protected with a private `object _gate`. No async, no queue — single-slot store.
- Publisher: `RunnerViewModel.RunAsync` — after `StrategyManager.RunBacktestFromFileAsync` returns, `_lastRun.Publish(new OverviewData(...))` is called before `RunCompleted?.Invoke`. Both success and failure paths publish.
- Consumer: `OverviewViewModel` — subscribes to `Changed` in the constructor and reads `Current` once at construction to seed the UI if a run already happened.
- Lifetime: process-scoped. Not persisted across app restarts. Each new run replaces the slot.

## 14. Monte Carlo Tab

- Purpose: run stationary block bootstrap over the trades of the most recent strategy run and display the resulting distributions.
- GUI (`src/gui/MonteCarlo/`):
  - `MonteCarloView.axaml` — Grid with toolbar (Row 0), source info banner (Row 1), main content (Row 2), status line (Row 3). Main content is `ColumnDefinitions="440,4,*"`: stats panel left, splitter, ScottPlot `AvaPlot` right.
  - `MonteCarloView.axaml.cs` — resolves `MonteCarloViewModel` via DI, calls `vm.SetPlotControl(PlotControl)` with the `x:Name`-ed AvaPlot instance.
  - `MonteCarloViewModel`:
    - Reads `LastRunStore.Current` for the trade list.
    - Reads `AccountService.Active` for starting capital and default ruin threshold (last phase in the active account's `Phases` array, `MaxLossPercent`).
    - Subscribes to `LastRunStore.Changed` and `AccountService.ActiveChanged`. Stale results are cleared on a new run.
    - `RunAsync` marshals to `Task.Run`, calls `MonteCarloEngine.Run(...)` with `IProgress<int>`, and populates `Stats` + plots on completion.
    - `McStatRow` — `Metric`, `Mean`, `Median`, `StdDev`, `P05`, `P95`. Display-only record.
- Toolbar controls: Iterations (`NumericUpDown`, default 1000, max 100000), Mean Block Size (default 5, max 100), Ruin Threshold % (default = active account's funded `MaxLossPercent`, else 50), Run button, ProgressBar (visible only while running), active account name label.
- Stats panel: six metric groups — Net Profit, Max Drawdown %, Annual Return %, Sharpe, Profit Factor, Max Consecutive Losses. Each shows Mean / Median / StdDev / p05 / p95 where the engine provides them. p05 is red, p95 is green.
- Plot panel: Net Profit histogram (40 bins) via `ScottPlot.Statistics.Histogram.WithBinCount`, plus vertical lines at p05 (red), median (white), p95 (green). No spaghetti equity curve yet — `MonteCarloResult` stores final values only, not per-simulation curves.
- `NumericUpDown` uses `ShowButtonSpinner="False"` to hide the up/down arrows. The spinner is not needed and clutters the toolbar.
- Tab position: `MainWindow.axaml` places Monte Carlo between Runner and Metrics.

### Deferred to a follow-up sub-phase (10.11C2)
- Spaghetti equity curve rendering. Requires `MonteCarloEngine` to store per-simulation equity curves (`double[][]`). Memory cost ~4 MB per 1000×500 run.
- Out-of-sample / in-sample split (moved to 10.11D).
- CSV / JSON export of results.

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
- Data tab layout:
  - Leftmost column: `On` checkbox (`DataGridTemplateColumn` with a `CheckBox` in the cell template).
  - Toolbar: `Refresh`, `Queue…`, and a live `N queued` counter bound to `QueuedCount`.
  - Status line: `"{Rows.Count} of {_all.Count} dataset(s) shown — {QueuedCount} queued."`
- Empty queue: legal state. Consumers (Runner, Chart) will refuse to run and show a hint. No consumer wiring has landed yet — that is Phase 10.14B/C.

## 16. Time-Domain Monte Carlo (Phase 10.15)

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

### Pending engine (Phase 10.15C)
The new `MonteCarloEngine` will consume the `TimeSlice[]` and implement:
- Stationary block bootstrap (Politis & Romano): block length drawn from a geometric distribution with mean `ExpectedBlockSize`. Circular wrap.
- Flat-bet absolute PnL compounding (`CurrentBalance += RealizedPnl`), not percentage. Prop firm capital is nominal and fixed-lot.
- State machine per slice: MTM low check against daily floor and max-DD floor, then realize PnL, then update HWM, then EOD reset.
- `DrawdownType` from the active account: `static`, `trailing-eod`, `trailing-intraday`. The HWM update semantics differ per type.
- Outputs: `P(ruin)`, `P(funded)`, `P(timeout)`, expected time to fund, expected time to ruin.
- Target duration from `AccountPhase.TradingPeriodDays` (fallback 90 days).
- Independent Monte Carlo runs per queued dataset. Aggregation sums expectations, does not pool trades.

### Why the old engine is being replaced
- Trade shuffling destroys volatility clustering and return autocorrelation.
- Intra-trade equity excursions are invisible. A position that spikes to -5.1% intra-candle but closes at +2% is scored a win, when the prop firm would have terminated the account.
- Static ruin thresholds understate risk on trailing-drawdown accounts like FTMO 1-Step.