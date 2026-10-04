<!-- docs\CONTEXT_ESSENTIALS.md -->
# backs2 — AI Context Essentials

Distilled reference for AI agents. The full detail lives in `docs\ARCHITECTURE.md`, `docs\PROJECT_CONTEXT.md`, and `docs\RULES.md` — request them via `Get-Content docs\ARCHITECTURE.md | Set-Clipboard` when the task needs a deep dive. This file is the single bundled reference; when it and a full doc disagree, the full doc wins (but only after it has been requested).

## 1. Project identity

- GUI: Avalonia 11.0.11 with CommunityToolkit.Mvvm 8.4.0 source generators.
- Core: .NET 8.0. DI-based xManager pattern.
- Namespaces: `backs2.Core.*` for logic, `backs2.ViewModels` for UI.
- All dependencies via constructor injection. No static globals. `LogManager` is the sole exception — static, never registered, never passed as a ctor param.

## 2. DI registrations (App.axaml.cs → ConfigureServices)

Singletons: `ConfigManager`, `CacheManager`, `DataManager`, `MetricRegistry`, `StrategyManager`, `StrategyCatalogService`, `PresetService`, `AccountService`, `LastRunStore`, `MonteCarloEngine`, `SelectionQueue`, `DataAutomationService`, `OcrWorkerClient`, `Mt5TickServer`, `Mt5TickService`, `ComputeWorkerManager`.

Transient ViewModels: `MainViewModel`, `ChartViewModel`, `DataViewModel`, `MetricsViewModel`, `RunnerViewModel`, `AccountViewModel`, `OverviewViewModel`, `MonteCarloViewModel`, `LogsViewModel`, `ConfigViewModel`, `ProvidersViewModel`, `DataAutomationViewModel`, `Mt5TickViewModel`.

Keyed transients (`IDataProvider`): `Twelve Data`, `Tiingo`, `Yahoo`, `Massive`, `Binance`, `Finnhub`, `FCS API`.

HTTP: one named client `ResilientClient` with `AddStandardResilienceHandler`. Providers must use it; never `new HttpClient()`.

## 3. Data pipeline (3 tiers)

`DataManager.GetSymbolDataAsync(symbol, timeframe, IProgress<DataProgress>, preferredProvider)`:
1. **Memory** — `IMemoryCache` keyed `symbol@timeframe`, value `MemoryEntry(Bars, Source)`.
2. **Disk** — `CacheManager.LoadCacheAsync(key)` at `data/cache/{symbol}_{timeframe}.json`, format `CacheEnvelope<KlineCompact[]>` via source-generated `KlineJsonContext`.
3. **Network** — walk `AppSettings.ProviderOrder`. First non-empty result wins. `preferredProvider` is hoisted to the front of the walk; a cache hit whose `source` differs from the preferred provider is bypassed and refetched.

`KlineCompact = { long T; double O,H,L,C,V; }` — T is Unix milliseconds. Envelope carries `source`, `fetchedUtc`, `symbol`, `timeframe`.

Symbol resolution is per-provider. Timeframe mapping is `TimeframeMap`. Never add symbol or timeframe translation to `DataManager` or `IDataProvider`.

`DataProgress(Message, IsDeterminate, Percent)`: RAM/disk hits report `IsDeterminate=false` (instant). The network walk reports `IsDeterminate=true` with `Percent = attemptIndex / totalProviders * 100`.

## 4. Metrics (IMetric / MetricRegistry)

- `IMetric`: `Name`, `Lag` (Leading/Lagging/Mixed), `Category` (Trend/Momentum/Volatility/Volume/Support/Breadth/Statistical), `Description`, `double Calculate(List<Kline>)`.
- Pure and stateless. Must never throw. Must return a defined value on empty, flat, or single-bar series.
- Registered at construction in `MetricRegistry`. `GetMetric(string)` never throws and never returns null — returns `NullMetric.Instance` on a miss.
- New metrics subclass `BaseMetric` and register in `MetricRegistry`.

Registered: VWAP, CVD, RSI(14), ATR(14), SessionHigh, ZScore, VP-POC, VP-VAH, VP-VAL.

## 5. Strategies (disk-streamed)

- Live in `data/strategies/*.cs`. Never compiled into the assembly (`Compile Remove` for `data\**\*.cs` in `backs2.csproj`).
- Executed by `StrategyManager` via Roslyn Scripting. Compiled runners cached by `(path, lastWriteUtc, size)`.
- Shape: top-level statements against `RunnerScriptGlobals`:
  - `Engine` (MetricRegistry) — `Engine.GetMetric("RSI").Calculate(Data)`.
  - `Data` (`List<Kline>`) — the loaded series.
  - `Log(string)` — delegates to `LogManager.Info`.
  - `Trades` (TradeRecorder) — `Advance(index)`, `Enter(side, price, size = 1.0)`, `Exit(price)`.
  - `Params` (`Dictionary<string,string>`) — user-configured values.
- Parameters: `// @param <Name> <Type> = <Default> [options: "A", "B"]` at the **TOP** of the file — above any `using` directives. `StrategyParameterParser` stops at the first non-comment, non-blank line.
- Types: `int`, `double`, `bool`, `string`. Parse doubles with `CultureInfo.InvariantCulture`.
- One open position at a time — `TradeRecorder._hasOpen` enforces it.
- Strategy parameters live in the source file. Never add a sidecar parameter file.
- Full authoring guide: request `docs\STRATS_GUIDE.md`.

## 6. Presets

- Files: `config/presets/{StrategyName}_{PresetName}.preset.json`.
- Managed exclusively through `PresetService`. Never write preset files directly.

## 7. Accounts

- Files: `data/accounts/{Firm}.account.json`. **One JSON per firm**, not per plan or size.
- Template: `data/accounts/_TEMPLATE.account.json`.
- Managed exclusively through `AccountService`.
- Active selection is a TRIPLE `(firmFileStem, plan, size)` written into `config/settings.json` via `AccountService.SetActive`.
- Types: `AccountPhase`, `FirmConfig`, `FirmPlan`, `PlanSize`, `AccountDescriptor`.
- No FileSystemWatcher — Refresh button on the Accounts tab.

## 8. Selection Queue

- `SelectionQueue` (singleton) is the **single source of truth** for enabled datasets.
- Persistence: `AppSettings.QueuedDatasets` as `"symbol@timeframe"` strings in `config/settings.json`.
- `DataRowViewModel.IsQueued` is the only writer.
- Modal `QueueWindow` uses pending-removal semantics: unchecking dims to 45% opacity, Done commits removals, Cancel discards, window X behaves like Done.
- Empty queue is legal. Consumers (Runner, Chart) refuse to run and show a hint.

## 9. Monte Carlo (time-domain)

- `TimeSlice` — `long Timestamp; double MtmHigh, MtmLow, RealizedPnl; byte IsEodReset`. One slice per Kline. `LayoutKind.Sequential`.
- `TimeSliceBuilder.Build(IReadOnlyList<Trade>, IReadOnlyList<Kline>) → TimeSlice[]` — pure static, allocation-free.
- `MonteCarloEngine.Run(...)`:
  - Stationary block bootstrap (Politis & Romano), geometric block length, circular wrap.
  - Flat-bet **absolute PnL compounding** (`CurrentBalance += RealizedPnl`), NOT percentage.
  - State machine per slice: MTM-low check vs daily floor + max-DD floor → realize PnL → update HWM → EOD reset.
  - `DrawdownType`: `static`, `trailing-eod`, `trailing-intraday`.

- Hard limits:
  - `MaxIterations = 100_000` (100× default). Enforced in the UI slider, a ViewModel guard that refuses to start, and the engine's own clamp.
  - `MaxCurveCells = 100_000_000`. Above that, equity curves are dropped (`CurvesRetained = false`); all scalar stats still populate.
  - Cancellation: `CancellationToken` flows into `ParallelOptions.CancellationToken`. Partial results from datasets that completed before cancel are kept.

- Aggregates: P(ruin)/P(funded)/P(timeout), terminal-balance distribution, max-DD distribution, best-day violation probability, losing/flat streak distributions, ruin-cause breakdown, Edge %.
- **Edge %** (random-signal null): the observed trade list is compared against a counterfactual where each trade's side is coin-flipped (same entry/exit indices, same size). `Edge = (strategyMean - nullMean) / StartingCapital * 100`. Requires 30+ trades. Null pass capped at 10,000 iterations, fixed seed 42, deterministic.
- UI is split across four partial-class files under `src/gui/MonteCarlo/`: `MonteCarloViewModel.cs` (core), `.Plots.cs`, `.Stats.cs`, `.Cluster.cs`. Each partial owns disjoint private fields; cross-partial invalidation flows through `InvalidateAll` on the core partial.
- Three plots stacked in the same cell, toggled by `IsVisible` bindings (`IsHistogramVisible` / `IsCurvesVisible` / `IsClusterVisible`): `HistogramPlot`, `CurvesPlot`, `ClusterPlot`.
- Cluster plot: `KMeansClusterer.Cluster` (4D: side, PnL%, duration minutes, entry hour-of-day), click → `NavigateToTradeMessage` via `WeakReferenceMessenger.Default`.
- **Never scramble trades directly.** Only resample `TimeSlice[]`. Percentage compounding is forbidden.

## 10. Compute Worker (Phase 1)

- Location: `src/core/Compute/` — `JobConfig`, `JobProgress`, `WorkerEnvelope`, `WorkerIpc`, `JobObjectHandle`, `WorkerHost`, `ComputeWorkerManager`.
- Worker is `backs2.exe --worker` — dispatched in `Program.Main` before Avalonia or `LogManager`. Writes nothing to `logs/`; diagnostics go to `Console.Error`, captured by the parent as `[worker <pid> stderr]` Debug lines.
- IPC: length-prefixed JSON frames over anonymous pipes (`WorkerIpc`, 4-byte LE prefix, 64 MB frame cap). Pipe directions are load-bearing: `serverOut` is parent-writes/worker-reads (`--in` on the worker), `serverIn` is parent-reads/worker-writes (`--out` on the worker). Swapping them causes immediate EOF.
- Memory cap: Win32 Job Object (`JOB_OBJECT_LIMIT_PROCESS_MEMORY`). Non-Windows degrades gracefully with a Warn log.
- Phase 1 runs a placeholder job. Phase 2 migrates `StrategyManager`. Phase 3 migrates `MonteCarloEngine`.
- `TestWorkerCommand` in the MC settings expander exercises the pipeline end-to-end.
- Do not introduce a second IPC dependency. Do not add Avalonia dependencies to the worker path.

## 11. Logging

- `LogManager` is a **static class** (`src/core/LogManager.cs`). The sole static in the codebase.
- Bootstrap: `Program.Main` calls `LogManager.Initialize()` before Avalonia. Log file is open before any XAML loads.
- Tags (closed enum, never add): `App`, `Cache`, `Data`, `Provider`, `Strategy`, `Runner`, `Account`, `MonteCarlo`, `Overview`, `Chart`, `Config`, `Metric`. Providers use `Provider` with the provider name embedded in the message.
- Calls: `LogManager.Info(LogTag.X, "msg")` / `.Warn` / `.Debug` / `.Error(tag, "msg", ex)`.
- Info = main flow, Debug = troubleshooting, Warn = recoverable abnormal, Error = exceptions with `ex`.
- No filtering. Every line goes to `logs/runtime-{ts}.log` and a 1000-entry ring buffer.
- Never register `LogManager` in DI. Never pass it as a ctor param.

## 12. Tabs

In `MainWindow.axaml` order: Overview, Runner, Monte Carlo, Metrics, Accounts, Data, Data Automation, MT5 Ticks, Providers, Chart, Logs, Config.

Numeric indices matter: `MainViewModel.ActiveTabIndex = 9` is Chart, used by cross-tab navigation.

## 13. Providers

- Contract: `Name`, `FetchAsync(symbol)`, `FetchAsync(symbol, timeframe)`. Return an empty `List<Kline>` on failure — **never throw**.
- Register as keyed transient in `App.axaml.cs`; the key must match the `Name` property exactly.
- Use `_httpClientFactory.CreateClient("ResilientClient")`.
- Log request traces at Debug, success at Info, skipped frames at Warn, exceptions at Error with `ex`.
- Symbol resolution inside the provider. Timeframe mapping inside `TimeframeMap`.
- Full integration guide: request `docs\PROVIDER_GUIDE.md`.

## 14. GUI / XAML rules

- ViewModels: `public partial class` inheriting `ObservableObject`.
- `[ObservableProperty]`, `[RelayCommand]` from CommunityToolkit.Mvvm.
- `x:DataType` on every View / UserControl root.
- `{Binding DataContext.Prop, RelativeSource={RelativeSource AncestorType=UserControl}}` inside templates. Never `$parent[UserControl].((vm:Type)DataContext).Prop`.
- `TextBlock` for clickable rows. Read-only `TextBox` for source preview / log output / status.
- DataGrid 11.0.11. ColorPicker 11.0.11 with `ColorSpectrumShape="Ring"`. ScottPlot.Avalonia 5.0.55.
- Partial-class convention for large ViewModels: `Mt5TickViewModel` (core + `.Extraction`, `.Requester`, `.Parsing`, `.Tkc`), `MonteCarloViewModel` (core + `.Plots`, `.Stats`, `.Cluster`).
- Keep file paths shallow. Windows MAX_PATH is a real constraint.

## 15. Keyboard navigation (all plots)

- `AvaPlot.UserInputProcessor` is disabled on every plot in the app. Mouse drag cannot pan or zoom; this is deliberate (the drag state could get stuck on some driver configurations).
- Arrow keys pan 10% of the visible span per press. `+` / `-` zoom the X-axis by 10%.
- Handlers are **tunnel-phase** on the UserControl root: `AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel)`. Tunnel phase fires before any focused child (ComboBox, ToggleButton) can consume the key.
- Text-entry controls (`TextBox`, `NumericUpDown`) keep their arrow keys — the guard walks the visual tree and returns early if the source is inside one.
- Never re-enable `UserInputProcessor`. Never attach the pan/zoom handler at bubble phase (breaks when a ComboBox has focus).

## 16. Cross-tab navigation

- `NavigateToTradeMessage(Symbol, Timeframe, TargetTime)` — `src/gui/Messages/NavigateToTradeMessage.cs`.
- Broadcast via `WeakReferenceMessenger.Default` (CommunityToolkit.Mvvm — no new dependency).
- Publisher: `MonteCarloViewModel.Cluster.cs` on cluster-plot click.
- Receivers register via `IRecipient<NavigateToTradeMessage>` and `WeakReferenceMessenger.Default.Register(this)`:
  - `MainViewModel` — sets `ActiveTabIndex = 9`.
  - `ChartViewModel` — selects the queued dataset, loads if needed, applies a 200-bar X-axis window centered on the target timestamp.
- Navigation does NOT mutate `SelectionQueue`. A request for an unqueued `Symbol @ Timeframe` produces a status-line warning.
- Never wire cross-tab navigation through direct ViewModel references.

## 17. File layout

- `config/` — user-authored (`settings.json`, `presets/`, `data_automation.json`). `data_automation.json` is git-ignored; the rest is tracked.
- `data/` — engine-managed: `cache/`, `strategies/`, `accounts/`, `shots/`, `parsed/`, `ticks/`.
- `docs/` — documentation plus the MQL5 bridge source (`Backs2TickBridge.mq5`) and range verifier (`VerifyTickRangeExact.mq5`).
- `logs/` and `.env` — git-ignored. Never commit secrets or log output.
- `src/core/Writers/` — writers that do not fit the standard `CacheEnvelope<KlineCompact[]>` shape (currently `Mt5TickWriter.cs`). Do not move `CacheManager` there.

## 18. Build protocol

- Compile-time change: `dotnet build --no-incremental 2>&1 | Select-Object -Last 15`. Success ends with `Build succeeded`.
- Runtime change: `dotnet run`, then after close `Get-ChildItem logs\ | Sort-Object LastWriteTime -Descending | Select-Object -First 1 | Get-Content`.
- Ask for build OR run — never both.
- Stale-source-generator symptoms (`AXSG0111`, phantom `CS1061` on generated members): wipe `obj/` and `bin/`, rebuild `--no-incremental` before touching code.

## 19. Hard invariants

- Never hard-code API keys. Never write secrets to `config/settings.json`. Read from `ConfigManager` (`.env`-sourced).
- `data/strategies/*.cs` must never compile into the assembly.
- `LogManager` is static; never in DI; never a ctor param.
- `SelectionQueue` is the only source of truth for enabled datasets. No parallel enabled-state.
- Monte Carlo operates in the time domain only. Never percentage-compound. Never scramble trades.
- Position size defaults to 1.0 in `Trades.Enter`. Strategies that need another size pass it explicitly.
- Active account is set only via `AccountService.SetActive`.
- Preset filenames encode the strategy name.
- Account JSON filenames encode the firm name. One firm per file.
- `Mt5TickViewModel.Requester.cs` physically lives under `src/core/MT5/` despite its `backs2.ViewModels` namespace. Known inconsistency; do not "fix" by moving without a dedicated refactor.
- Providers return empty on failure, never throw.
- `KMeansClusterer.Cluster` is deterministic (fixed seed 42). Do not change the seed.
- Compute worker: length-prefixed JSON over anonymous pipes. Never add a second IPC dependency.
- Never re-enable ScottPlot `UserInputProcessor`.

## 20. Docs on demand

The full doc set is not bundled. Request individual files with `Get-Content <path> | Set-Clipboard` when the task needs a deep dive. The packer's "DOCS NOT BUNDLED" section lists every omitted file and its request command.