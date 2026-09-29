<!-- docs\RULES.md -->
ONLY SPEAK IN ENGLISH

# Development Rules
- ALWAYS check namespace before writing code (backs2.Core for logic, backs2.ViewModels for UI).
- ViewModels: public partial class inheriting ObservableObject.
- AXAML: always set x:Class="backs2.Views.YourView" and x:DataType for compiled bindings.
- Keep file paths shallow.
- Style: High-Performance C# — minimal allocations, List<T> over LINQ in hot paths.
- Architecture:
    - No static global variables. Enforce xManager/DI.
    - All I/O non-blocking (Async/Await).
    - UI progress via IProgress<T>.
    - Strictly typed bindings required for all AXAML.
- Metrics:
    - Every `IMetric` must return a defined value for degenerate inputs and must never throw.
    - Metrics are pure and stateless.
    - `MetricRegistry.GetMetric` returns `NullMetric.Instance` on a miss.
    - New metrics: subclass `BaseMetric`, supply `Name`, `Lag`, `Category`, `Description`, register in `MetricRegistry`.
- Settings & Dynamic UI:
    - To add an engine setting, declare a public property on `AppSettings`. It auto-renders in the Config tab.
    - Supported types (`int`, `double`, `bool`, `string`, `Color`) auto-render.
    - Do NOT hardcode XAML controls for `AppSettings` properties.
    - For non-secret on-disk state, resolve the config root via `ConfigManager.ConfigDirectory`.
- Secrets:
    - Never hard-code API keys. Never write secrets to config/settings.json.
    - Read from ConfigManager read-only properties, sourced from .env.
- Providers:
    - Implement IDataProvider; register as keyed transient in App.axaml.cs.
    - Symbol resolution inside the provider. Timeframe mapping inside TimeframeMap.
    - Return an empty List<Kline> on failure; never throw.
    - Name is human-readable and shown in the Config tab.
- Caching:
    - Disk format is `CacheEnvelope` wrapping `KlineCompact[]`, serialized via `KlineJsonContext`.
    - Never add a new cache format without a schema/version detection path in `LoadCacheAsync`.
    - Cache keys are `symbol@timeframe`.
- Strategies:
    - Live in data/strategies/, one file per strategy, top-level statements.
    - Globals: Engine, Data, Log, Trades, Params.
    - Parameters declared at the top of the file with `// @param <Name> <Type> = <Default> [options: "A", "B"]`.
    - Never place a strategy .cs outside data/strategies/.
    - Trade recording: `Trades.Advance(index)` before `Enter` / `Exit`. `Enter(side, price, size = 1.0)`.
- Presets:
    - Live in config/presets/{StrategyName}_{PresetName}.preset.json.
    - Manage exclusively through PresetService.
- Accounts:
    - One JSON per firm at data/accounts/{Firm}.account.json.
    - Manage exclusively through AccountService.
    - Active selection is a triple in settings.json.
    - Never split a firm across multiple files.
- Selection Queue:
    - `SelectionQueue` is the only place enabled-dataset state lives.
    - `DataRowViewModel.IsQueued` is the only writer.
    - Queue modal pending-removal is committed on Done, discarded on Cancel.
- Monte Carlo:
    - Operates in the time domain. Never scramble trades directly.
    - Build `TimeSlice[]` via `TimeSliceBuilder.Build(trades, klines)`. One slice per Kline.
    - Block bootstrap: stationary (Politis & Romano), geometric block length, circular wrap.
    - Compound as `CurrentBalance += RealizedPnl`. Never percentage-compound. Prop firm capital is nominal.
    - Evaluate prop-firm rules per slice against MTM low, not closed-trade PnL.
    - `DrawdownType` from the active account: `static`, `trailing-eod`, `trailing-intraday`. Never ignore it.
    - Batch: independent runs per queued dataset. Aggregate by summing expectations, not by pooling trades.
- Logging:
    - Write via `LogManager.Info(LogTag.X, msg)` / `Warn` / `Debug` / `Error`.
    - `LogManager` is static. Never register it in DI. Never pass it as a ctor parameter.
    - Tags: App, Cache, Data, Provider, Strategy, Runner, Account, MonteCarlo, Overview, Chart, Config, Metric.
    - Debug for troubleshooting-only lines. Info for main flow. Warn for recoverable abnormal. Error for exceptions with `ex`.
- XAML binding inside templates:
    - Use Binding DataContext.Prop with RelativeSource AncestorType=UserControl.
    - Do not use `$parent[UserControl].((vm:Type)DataContext).Prop`.
- Charting:
    - ScottPlot.Avalonia 5.0.55 is the standard.
- Build vs Run Protocol:
    - Build only (compile-time changes): `dotnet build --no-incremental 2>&1 | Select-Object -Last 15`. Success ends with `Build succeeded`.
    - Build + run (runtime changes): `dotnet run`, then after close: `Get-ChildItem logs\ | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
    - Ask for build OR run, never both.
    - If the app crashed before `LogManager.Initialize`, there is no new log file — ask for terminal output instead.

## Data Automation
- Window discovery uses EnumWindows + PID match. No injection, no hooks.
- Input uses SendInput via P/Invoke. Never a NuGet input library.
- Capture uses PrintWindow with PW_RENDERFULLCONTENT. Never DXGI Desktop Duplication.
- Both calibrations (region, click point) persist to config/data_automation.json, which is git-ignored.
- Run order: focus+maximize -> click -> 200x right-arrow -> capture+left-arrow loop.
- Output: data/shots/{symbol}_{timeframe}_{index:D4}.png.
- The view's code-behind owns the calibration overlay. Do not extract it to a standalone .axaml.
- Do not commit config/data_automation.json.

## Commit Convention
When a phase is completed, ship a commit with the message format:

    <type>(<scope>): <summary>

    - <bullet 1>
    - <bullet 2>

Types: feat, fix, perf, docs, refactor, chore, wip.
Scope: providers, cache, strategies, logs, data, chart, config, metrics, gui, runner, presets, accounts, overview, montecarlo, queue.

Summary: one line, imperative mood, no trailing period. Under 72 characters.

After the commit, print the last three log lines:

    git log --oneline -3
    git status

The working tree must be clean.