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
    - Every `IMetric` must return a defined value for degenerate inputs (empty list, flat series, single bar) and must never throw.
    - Metrics are pure and stateless: same input, same output. No memoisation, no caches, no cross-call state inside a metric or the engine.
    - `MetricRegistry.GetMetric` returns `NullMetric.Instance` on a miss (`Calculate => 0`). It never throws and never returns null, so strategy scripts can call `.Calculate(...)` unconditionally.
    - New metrics: subclass `BaseMetric`, supply `Name`, `Lag`, `Category`, `Description`, register in `MetricRegistry`.
- Settings & Dynamic UI:
    - To add an engine setting, simply declare a public property on `AppSettings`.
    - Supported types (`int`, `double`, `bool`, `string`, `Color`) auto-render into appropriate controls in Config tab (`NumericUpDown`, `ToggleSwitch`, `TextBox`, `ColorPicker Ring`).
    - Do NOT hardcode XAML controls for `AppSettings` properties.
    - For non-secret on-disk state that is not an `AppSettings` property, resolve the config root via `ConfigManager.ConfigDirectory`. Do not hardcode `"config"` outside `ConfigManager`.
- Secrets:
    - Never hard-code API keys.
    - Never write secrets to config/settings.json.
    - Read from ConfigManager read-only properties, sourced from .env.
- Providers:
    - Implement IDataProvider; register as keyed transient in App.axaml.cs.
    - Providers are automatically discovered by `ConfigViewModel` via assembly reflection.
    - Do symbol resolution inside the provider. Do not add symbol translation to DataManager or IDataProvider.
    - Do timeframe wire-format mapping inside TimeframeMap, not inside individual providers.
    - Return an empty List<Kline> on failure; never throw — DataManager uses empty as the signal to fall back.
    - Name is human-readable and shown in the Config tab.
- Caching:
    - Disk format is KlineCompact[], serialized via KlineJsonContext.
    - Never add a new cache format without a schema/version detection path in LoadCacheAsync.
    - Cache keys are symbol@timeframe.
- Strategies:
    - Live in data/strategies/, one file per strategy, top-level statements.
    - Globals: Engine (MetricRegistry), Data (List<Kline>), Log(string), Trades (TradeRecorder), Params (Dictionary<string,string>).
    - Parameters declared at the top of the file with `// @param <Name> <Type> = <Default> [options: "A", "B"]`.
    - Supported types: int, double, bool, string. `options:` is only valid on string params; it renders a ComboBox.
    - Never place a strategy .cs outside data/strategies/.
    - Never add a strategy file to a Compile item. Strategies are Roslyn-evaluated, not compiled.
    - Trade recording: call `Trades.Advance(index)` before `Enter` / `Exit` so timestamps come from the correct Kline. Do not write trades to disk manually.
- Presets:
    - Live in config/presets/{StrategyName}_{PresetName}.preset.json.
    - Manage exclusively through PresetService. Do not write preset files directly.
    - The filename is a lossy transport; the JSON payload is the source of truth for the preset name.
- Runner:
    - Runner GUI lives in src/gui/Runner/.
    - Runner core types live in src/core/Runner/.
    - RunnerScriptGlobals is the script contract. Add new globals there, not in StrategyManager.
    - Parameter parsing never executes the strategy file; it only reads `// @param` lines up to the first non-comment line.
- XAML binding inside templates:
    - Use Binding DataContext.Prop with RelativeSource AncestorType=UserControl.
    - Do not use $parent[UserControl].((vm:Type)DataContext).Prop — fails at runtime inside templates.
- Selecting text in the UI:
    - TextBlock for clickable list rows.
    - Read-only TextBox only where copy/paste is required (source preview, log output, status line).
- Logs:
    - Write via LogManager.Info / LogManager.Error.
    - logs/ is git-ignored. One log file per app run: runtime-{timestamp}.log.
- Charting:
    - ScottPlot.Avalonia 5.0.55 is the standard for hardware-accelerated SkiaSharp charting.

## Commit Convention
When a phase is completed, ship a commit with the message format:

    <type>(<scope>): <summary>

    - <bullet 1>
    - <bullet 2>
    ...

Types:
- feat      new functionality
- fix       bug fix
- perf      performance work
- docs      documentation only
- refactor  internal restructuring, no behavior change
- chore     build, deps, config, non-code
- wip       work in progress, not yet complete (used for blocked phases)

Scope:
- The affected subsystem: providers, cache, strategies, logs, data, chart, config, metrics, gui, runner, presets.

Summary: one line, imperative mood, no trailing period. Under 72 characters.

Body: optional bullets listing the concrete changes. Include phase references when applicable, e.g. "(Phase 10.5)".

After the commit, print the last three log lines to confirm:

    git log --oneline -3
    git status

The working tree must be clean. If git status shows untracked or modified files, either stage them or add them to .gitignore before finalizing.