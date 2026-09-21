# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: `Microsoft.Extensions.DependencyInjection` managed in `App.axaml.cs`.
- Service Lifecycle: 
    - Singletons: `LogManager`, `ConfigManager`, `DataManager`, `StrategyManager`, `StrategyCatalogService`, `MetricRegistry`, `CacheManager`.
    - Transients/Keyed: `IDataProvider` implementations (resolved via `[FromKeyedServices]` / `GetRequiredKeyedService`).
- Enforcement: No static global variables allowed. All dependencies are resolved via constructor injection.

## 2. Data Pipeline (3-Tier)
1. **Memory Tier (`IMemoryCache`):** RAM-based fast lookup for immediate data access.
2. **Persistence Tier (`CacheManager`):** Async JSON/Parquet storage at `data/cache/`.
3. **Network Tier (`IDataProvider`):** Abstracted interface with automatic resilience.
   - Resilience: All providers use a named `"ResilientClient"` (configured via `AddStandardResilienceHandler`) for auto-retries and timeouts based on `ConfigManager` settings.
- Flow: `DataManager` checks Memory → Disk → Network.
- **Provider Fallback:** The network tier is a **fallback chain** driven by `AppSettings.ProviderOrder` (ordered list of provider keys). `DataManager` walks the list in order, and the first provider that returns a non-empty `List<Kline>` wins. Failures and empty results are logged and the next provider is tried. Errors never abort the chain.

## 3. Metrics Engine
- `IMetric` Interface: Base template for all mathematical indicators.
- `MetricRegistry`: Centralized service for dynamic strategy queries.
- Implementation: Derived metrics (VWAP, Z-Score) are injected into the registry at startup.

## 4. Strategy Execution (Disk-Streamed)
- **Source of truth:** `data/strategies/*.cs` on disk. There is no in-app strategy builder.
- **Catalog:** `StrategyCatalogService` (singleton) scans `data/strategies/` on startup and watches it via `FileSystemWatcher` (debounced 250 ms). It exposes:
  - `Current` — read-only snapshot of `StrategyDescriptor` entries (path, name, last-write UTC, size).
  - `Changed` — event raised after every catalog refresh.
  - `ReadSourceAsync(path)` — lazy source load, called only when a strategy is clicked.
  - `Refresh()` — manual full rescan.
  - Auto-creates `data/strategies/` if missing.
- **Execution:** `StrategyManager` uses the Roslyn Scripting API (`CSharpScript.Create<T>(...).CreateDelegate()`) to compile each file in memory. Compiled runners are cached by `(path, lastWriteUtc, size)`, so edits invalidate automatically.
- **Concurrency:** `SemaphoreSlim(1,1)` throttles backtests to one at a time.
- **Script Shape:** Top-level statements. Scripts run against `ScriptGlobals`:
  - `Engine` (`MetricRegistry`) — access metrics via `Engine.GetMetric("VWAP")`.
  - `Data` (`List<Kline>`) — the loaded series.
  - `Log(string)` — delegates to `LogManager.Info`.
- **Build rule:** `backs2.csproj` contains `<Compile Remove="data\**\*.cs" />`. Strategies are Roslyn-evaluated at runtime and must never be compiled into the assembly.

## 5. Configuration & Settings
- **Settings File:** `ConfigManager` handles `config/settings.json` — non-secret engine state (threading, resilience, provider order).
- **Secrets:** API keys are read from `.env` at startup and exposed as read-only `ConfigManager` properties (`MassiveApiKey`, `YahooApiKey`). Secrets are never persisted to `settings.json`.
- **Provider Order:** `AppSettings.ProviderOrder` is an ordered list of provider keys (e.g. `["Massive", "Yahoo"]`). Edited via the Config tab. Consumed by `DataManager`.

## 6. Providers (`IDataProvider`)
- Contract: `string Name { get; }` and `Task<List<Kline>> FetchAsync(string symbol)`.
- Each provider is registered as a keyed transient in `App.axaml.cs` (`AddKeyedTransient<IDataProvider, XProvider>("Key")`).
- **Symbol resolution is per-provider.** Each provider translates user-facing tickers to its own convention:
    - Yahoo: `XAUUSD → XAUUSD=X`, `EURUSD → EURUSD=X`, `AAPL → AAPL`
    - Massive: `XAUUSD → C:XAUUSD`, `EURUSD → C:EURUSD`, `AAPL → AAPL`
- **Massive provider:**
    - Endpoint: `GET https://api.massive.com/v2/aggs/ticker/{ticker}/range/1/day/{from}/{to}?adjusted=true&sort=asc&limit=50000&apiKey=…`
    - Response fields: `t` (ms timestamp), `o`, `h`, `l`, `c`, `v`, `vw`, `n`.
    - Status handling: only `NOT_AUTHORIZED`, `ERROR`, and `NOT_FOUND` are fatal. `OK` and `DELAYED` both carry usable `results[]` and are parsed.
- **Yahoo provider:**
    - Endpoint: `GET https://query1.finance.yahoo.com/v8/finance/chart/{symbol}?interval=1d&range=1mo`
    - Response parsing walks `chart.result[0].timestamp` + `chart.result[0].indicators.quote[0].{open,high,low,close,volume}`.
    - Skips null bars (Yahoo returns nulls on market-closed days).
- **Secrets:** providers read keys from `ConfigManager.MassiveApiKey` / `ConfigManager.YahooApiKey` — never from `AppSettings`.

## 7. GUI (MVVM Toolkit)
- **Dependency Injection:** ViewModels are resolved via the DI container in the View constructor. 
    - Pattern: `DataContext = app.Services.GetRequiredService<TViewModel>();`
- **Source Generation:** All ViewModels use `CommunityToolkit.Mvvm` attributes (`[ObservableProperty]`, `[RelayCommand]`).
- **XAML Bindings:** 
    - `x:DataType` must be set on all View/UserControl roots.
    - `MainWindow.axaml` sets `x:CompileBindings="False"` on the `TabControl` so child views with their own `x:DataType` are treated as self-contained.
    - If `AXSG0111` errors appear, perform `dotnet clean && dotnet build` so the source generators populate properties.
- **Selectable text policy:** Read-only `TextBox` is used where the user must be able to copy text (source preview, log output, status line). Clickable list rows use `TextBlock` so the click reaches the parent `ListBox`.

## 8. Tabs
- **Chart** — plotting.
- **Metrics** — metric catalog browser.
- **Data** — dataset browser.
- **Strategies** — disk-streamed strategy catalog; click a row to preview source, "Run Selected" to execute against `DataManager` data.
- **Logs** — live-refreshing log view (2 s `DispatcherTimer`), reads via `LogManager.GetLatestLogs()`.
- **Config** — settings editor, including provider order UI (add/remove/reorder).

## 9. Logging
- `LogManager` wraps Serilog writing to `logs/runtime.log` (rolling daily).
- `GetLatestLogs()` returns the last 100 lines, reversed (newest first), and returns an empty list if the file does not yet exist (first launch).
- `logs/` is git-ignored.