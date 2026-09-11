# Architecture: backs2 Trading Engine

## 1. Core Services & Dependency Injection
- DI Container: `Microsoft.Extensions.DependencyInjection` managed in `App.axaml.cs`.
- Service Lifecycle: 
    - Singletons: `LogManager`, `ConfigManager`, `DataManager`, `StrategyManager`, `MetricRegistry`, `CacheManager`.
    - Transients/Keyed: `IDataProvider` implementations (resolved via `[FromKeyedServices]`).
- Enforcement: No static global variables allowed. All dependencies are resolved via constructor injection.

## 2. Data Pipeline (3-Tier)
1. **Memory Tier (`IMemoryCache`):** RAM-based fast lookup for immediate data access.
2. **Persistence Tier (`CacheManager`):** Async JSON/Parquet storage at `data/cache/`.
3. **Network Tier (`IDataProvider`):** Abstracted interface with automatic resilience.
   - Resilience: All providers use a named `"ResilientClient"` (configured via `AddStandardResilienceHandler`) for auto-retries and timeouts based on `ConfigManager` settings.
- Flow: `IDataProvider` -> `DataManager` -> `CacheManager` -> `IMemoryCache` -> `StrategyManager`.

## 3. Metrics Engine
- `IMetric` Interface: Base template for all mathematical indicators.
- `MetricRegistry`: Centralized service for dynamic strategy queries.
- Implementation: Derived metrics (VWAP, Z-Score) are injected into the registry at startup.

## 4. Strategy Execution
- Roslyn Scripting API: Compiles strategies in-memory.
- Execution: `StrategyManager` uses `SemaphoreSlim` to throttle concurrent backtests to protect memory and CPU.
- Script Context: Strategies have access to `ScriptGlobals` (containing `Engine` and `Data`).

## 5. Configuration & Settings
- Centralized: `ConfigManager` handles `settings.json`.
- Settings Model: All global engine variables (Retries, Timeouts, Keys) are stored in `AppSettings` within `ConfigManager`.

## 6. GUI (MVVM Toolkit)
- Pattern: Avalonia View (AXAML) + partial ViewModel (C#).
- MVVM Toolkit: `ObservableObject` base class, `[ObservableProperty]`, `[RelayCommand]`.
- Navigation: `MainWindow` acts as the host with a `TabControl`.
- Async UI: All heavy operations (I/O, backtests) use `Task.Run` and `IProgress<string>` for non-blocking status updates.