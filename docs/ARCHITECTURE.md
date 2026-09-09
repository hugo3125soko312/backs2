# Architecture: backs2 Trading Engine
## 1. Core Services (xManager Pattern)
- Singletons: ConfigManager, DataManager, StrategyManager, LogManager.
- Dependency Injection: Managed in `App.axaml.cs`. All services are injected into the `MainViewModel`.
- Data Flow:
    - Provider (Yahoo/API) -> DataManager (CSV/Parquet) -> Memory/RAM (StrategyManager).
- Strategy Execution: 
    - Roslyn Scripting API executes dynamic code from `strats/`. 
    - `StrategyManager` uses `SemaphoreSlim` to throttle concurrent backtests.

## 2. GUI (MVVM Toolkit)
- Pattern: View (AXAML) + ViewModel (C#).
- ViewModels: `ObservableObject` base, uses `[ObservableProperty]` and `[RelayCommand]`.
- Navigation: `MainWindow` contains a `TabControl` that switches between `Views`.