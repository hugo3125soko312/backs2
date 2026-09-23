# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: Microsoft.Extensions.DependencyInjection, managed in App.axaml.cs.
- Service Lifecycle:
    - Singletons: LogManager, ConfigManager, DataManager, StrategyManager, StrategyCatalogService, MetricRegistry, CacheManager.
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
- Progress reporting: DataManager.GetSymbolDataAsync(symbol, timeframe, IProgress<DataProgress>) reports DataProgress(Message, IsDeterminate, Percent). RAM/disk hits report IsDeterminate=false (instant). The network tier reports IsDeterminate=true with Percent = attemptIndex / totalProviders * 100. A legacy IProgress<string> overload adapts to the new contract for consumers that only need text (e.g. StratsViewModel).

## 3. Metrics Engine
- IMetric Interface: Base template for all mathematical indicators.
- MetricRegistry: Centralized service for dynamic strategy queries.
- Implementation: Base and derived metrics (VWAP, Z-Score, SessionHigh) are injected into the registry at startup.

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
- Script Shape: Top-level statements running against ScriptGlobals:
  - Engine (MetricRegistry) — access metrics via Engine.GetMetric("VWAP").
  - Data (List<Kline>) — the loaded series.
  - Log(string) — delegates to LogManager.Info.
- Build rule: backs2.csproj contains a Compile Remove for data\**\*.cs. Strategies are Roslyn-evaluated at runtime and must never be compiled into the assembly.

## 5. Configuration & Settings
- Settings File: ConfigManager handles config/settings.json — non-secret engine state (threading, resilience, provider order).
- Secrets: API keys are read from .env at startup and exposed as read-only ConfigManager properties (MassiveApiKey, YahooApiKey). Secrets are never persisted to settings.json.
- Provider Order: AppSettings.ProviderOrder is an ordered list of provider keys (e.g. ["Massive", "Yahoo"]). Edited via the Config tab. Consumed by DataManager.
- Timeframes: Canonical list lives in TimeframeMap.All: 1m, 2m, 3m, 5m, 15m, 30m, 45m, 1h, 2h, 3h, 4h, 1D, 1W.

## 6. Providers (IDataProvider)
- Contract:
  - Name (string)
  - Task<List<Kline>> FetchAsync(string symbol)
  - Task<List<Kline>> FetchAsync(string symbol, string timeframe)
- Each provider is registered as a keyed transient in App.axaml.cs.
- Symbol resolution is per-provider:
    - Yahoo: XAUUSD -> XAUUSD=X, EURUSD -> EURUSD=X, AAPL -> AAPL
    - Massive: XAUUSD -> C:XAUUSD, EURUSD -> C:EURUSD, AAPL -> AAPL
- Timeframe resolution is centralized in TimeframeMap.
    - ToMassive(tf) -> (multiplier, timespan). All canonical timeframes supported.
    - ToYahoo(tf) -> interval string or null. Yahoo does not support 3m, 45m, 2h, 3h, 4h natively; those return null and the provider logs an error and returns an empty list.
    - LookbackDays(tf) — per-timeframe fetch window.
- Massive provider:
    - Endpoint: GET https://api.massive.com/v2/aggs/ticker/{ticker}/range/{multiplier}/{timespan}/{from}/{to}?adjusted=true&sort=asc&limit=50000&apiKey=...
    - Response fields: t (ms timestamp), o, h, l, c, v, vw, n.
    - Status handling: only NOT_AUTHORIZED, ERROR, and NOT_FOUND are fatal. OK and DELAYED both carry usable results[] and are parsed.
- Yahoo provider:
    - Endpoint: GET https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval={interval}&range={range}
    - Response parsing walks chart.result[0].timestamp and chart.result[0].indicators.quote[0].{open,high,low,close,volume}.
    - Skips null bars (Yahoo returns nulls on market-closed days).
- Secrets: providers read keys from ConfigManager.MassiveApiKey / ConfigManager.YahooApiKey — never from AppSettings.

## 7. Caching
- Disk format: KlineCompact record — flat {T, O, H, L, C, V} with T as Unix milliseconds. Serialized via source-generated KlineJsonContext.
- Legacy-format detection: LoadCacheAsync returns null on empty files, malformed JSON, or all-zero payloads, so DataManager refetches.
- Per-file log lines: "Cache: saved {key} ({n} bars, {ms} ms)" and "Cache: loaded {key} ({n} bars, {ms} ms)".
- Memory-tier observability: DataManager logs "RAM hit" / "RAM miss" for every lookup.
- Benchmark: CacheManager.BenchmarkAsync(int barCount) serializes and loads N synthetic bars, logging elapsed milliseconds.

## 8. GUI (MVVM Toolkit)
- Dependency Injection: ViewModels resolved in the View constructor:
  if (Application.Current is App app)
      DataContext = app.Services.GetRequiredService<TViewModel>();
- Source Generation: All ViewModels use CommunityToolkit.Mvvm attributes ([ObservableProperty], [RelayCommand]).
- XAML Bindings:
    - x:DataType on every View/UserControl root.
    - MainWindow.axaml sets x:CompileBindings="False" on the TabControl so child views with their own x:DataType are self-contained.
    - If AXSG0111 errors appear, wipe obj/ and bin/, then dotnet build --no-incremental.
- DataGrid use: Avalonia.Controls.DataGrid 11.0.11 is referenced. Its theme is included in App.axaml:
    StyleInclude Source="avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml"
- Binding inside templates: use Binding DataContext.Prop with RelativeSource AncestorType=UserControl. Do not use $parent[UserControl].((vm:Type)DataContext).Prop — it fails at runtime inside DataTemplate/HeaderTemplate because the XAML runtime type resolver cannot see the vm prefix from within the template scope.
- Selectable text policy: TextBlock for clickable list rows; read-only TextBox for source preview, log output, status line.

## 9. Tabs
- Chart — OxyPlot.Avalonia 2.1.0-Avalonia11 candlestick + volume, dark theme, custom tracker, scale context menu, timeframe switching (Phase 10.7C), crosshair + OHLCV readout (Phase 10.7D). Crosshair is drawn as Avalonia overlay Rectangles positioned via TranslateTransform, NOT as PlotModel annotations, so hover does not invalidate the plot. Pointer handling lives in ChartView.axaml.cs. KNOWN BLOCKER: pan/zoom and hover lag. Root cause is that this OxyPlot.Avalonia package renders through the generic DrawingContext pipeline and re-renders the entire PlotModel on every invalidation. The CreateRenderContext/XamlRenderContext override that fixes this is not present in this package build (CS0115), and no released NuGet package provides the SkiaSharp render path for Avalonia 11.0.11. See docs/ROADMAP.md Phase 10.7F.
- Metrics — metric catalog browser.
- Data — Access-style datasheet. DataGrid with per-column filter rows embedded in the header, sort via header buttons, size formatting via DataEntry.SizeDisplay.
- Strategies — disk-streamed catalog. Click a row to preview source, choose a symbol, "Run Selected".
- Logs — event-driven live log view driven by LogManager.Logged. In-memory ring buffer (1000 entries), 2000-line display cap.
- Config — settings editor, provider order.

## 10. Logging
- LogManager writes via Serilog to logs/runtime-{yyyyMMdd-HHmmss}.log — one file per app run, no daily rotation.
- Logged event raised in-process on every Info / Error.
- Ring buffer: last 1000 entries via GetRecentEntries().
- GetLatestLogs() reads the current run's file with FileShare.ReadWrite | FileShare.Delete.
- logs/ is git-ignored.