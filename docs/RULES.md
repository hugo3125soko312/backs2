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
- Secrets:
    - Never hard-code API keys.
    - Never write secrets to config/settings.json.
    - Read from ConfigManager.MassiveApiKey / ConfigManager.YahooApiKey, sourced from .env.
- Providers:
    - Implement IDataProvider; register as keyed transient in App.axaml.cs.
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
    - Globals: Engine (MetricRegistry), Data (List<Kline>), Log(string).
    - Never place a strategy .cs outside data/strategies/.
    - Never add a strategy file to a Compile item. Strategies are Roslyn-evaluated, not compiled.
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
    - ScottPlot.Avalonia 5.0.55 is referenced but currently blocked — see docs/ROADMAP.md Phase 10.7.
    - Do not add new charting dependencies without first resolving Phase 10.7.

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
- The affected subsystem: providers, cache, strategies, logs, data, chart, config, metrics, gui.

Summary: one line, imperative mood, no trailing period. Under 72 characters.

Body: optional bullets listing the concrete changes. Include phase references when applicable, e.g. "(Phase 10.5)".

After the commit, print the last three log lines to confirm:

    git log --oneline -3
    git status

The working tree must be clean. If git status shows untracked or modified files, either stage them or add them to .gitignore before finalizing.