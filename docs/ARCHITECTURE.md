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
- LogManager writes via Serilog to logs/runtime-{yyyyMMdd-HHmmss}.log.

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