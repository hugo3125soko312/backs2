# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.10: Account Configuration [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md`. One JSON per firm, triple-based active selection.

## Phase 10.11: Monte Carlo [IN PROGRESS]

### Phase 10.11A -- LastRunStore & OverviewData [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11B -- Monte Carlo Core [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11C -- Monte Carlo Tab GUI [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11C2 -- Equity Curve Rendering [PENDING]
- [ ] Extend `MonteCarloEngine` to store per-simulation equity curves (`double[][]`).
- [ ] Memory budget: 1000 iterations × 500 trades × 8 bytes = ~4 MB per run. Document it in `MonteCarloResult`.
- [ ] Spaghetti plot on the Monte Carlo tab: base curve (bold), p05 (red), p95 (green), plus a configurable "straw broom" count (default 20).
- [ ] Render only the 3 fixed lines + straw broom sample, never all 1000. `Signal` series, not `Scatter`.

### Phase 10.11D -- Overview Tab (Full)
- [ ] Full in-sample metrics table: CAGR, Expectancy, Sharpe, Sortino, Profit Factor, Win Rate, Avg Duration, Max DD.
- [ ] Monte Carlo summary: Risk of Ruin, p05/p50/p95 for Net Profit and Max DD.
- [ ] Three-column header (In-Sample / Monte Carlo / Out-of-Sample — OOS deferred).
- [ ] Color-coded cells.
- [ ] Reads `LastRunStore.Current` and the latest `MonteCarloResult` (cached in a shared store, not recomputed).

### Dependencies
- 10.11A before 10.11B and 10.11C.
- 10.11B before 10.11C.
- 10.11C before 10.11C2 and 10.11D.

---

## Phase 10.7: Chart Tab [IN PROGRESS]

See `docs/ROADMAP_ARCHIVE.md` for completed sub-phases (10.7A, 10.7B, 10.7F).

### Phase 10.7C -- Timeframe Switching [PENDING]
- [ ] Timeframe picker: `1m`, `5m`, `15m`, `1h`, `4h`, `1D`, `1W`.
- [ ] Fetch per-timeframe from provider, cache under `symbol@timeframe`.

### Phase 10.7D -- Crosshair & Readout [PENDING]
- [ ] Mouse crosshair tracking.
- [ ] O/H/L/C/Volume/timestamp readout box.

### Phase 10.7E -- Polish [IN PROGRESS]
- [ ] Save/restore last symbol + timeframe.
- [ ] Empty state, error state, selectable error texts.
- [ ] Lock price-to-bar ratio.

---

## Phase 10.12: Tagged Logging Refactor [PLANNED]

Replace the current message-prefix convention (`"Cache: loaded X"`, `"Strategy failed: ..."`) with first-class severity + subsystem tags. Serilog already supports this; the wrapper hides it. Goal: filterable, queryable logs without breaking the no-allocation-in-hot-paths rule.

### Decisions locked (from planning)
- `LogTag` is an enum, not a string. Compile-time safety beats flexibility for a fixed subsystem set.
- One `Provider` tag, not `Provider:Tiingo`. Provider name travels as a structured field, not an enum value. New providers do not require enum edits.
- Two overloads: `Info(tag, message)` and `Info(tag, message, params (string, object)[] fields)`.
- Second Serilog sink writes `logs/runtime-{timestamp}.json` alongside the text log.
- `Debug` level included, gated by a `LogLevel` filter.
- `LogLevel` exposed as an `AppSettings` property, auto-rendered in the Config tab.

### Phase 10.12A — LogManager API
- [ ] `LogTag` enum: App, Cache, Data, Provider, Strategy, Runner, Account, MonteCarlo, Overview, Chart, Config, Metric.
- [ ] `LogManager.Info(LogTag, string)`, `Warn`, `Error(LogTag, string, Exception? = null)`, `Debug`.
- [ ] Structured-property overloads: `Info(LogTag, string, params (string, object)[] fields)`.
- [ ] Serilog output template includes the tag: `[INF] [Cache] message`.
- [ ] Second JSON sink configured for `logs/runtime-{timestamp}.json`.

### Phase 10.12B — Call Site Migration
- [ ] `src/core/DataManager.cs` — tag `Data`.
- [ ] `src/core/CacheManager.cs` — tag `Cache`.
- [ ] `src/core/StrategyManager.cs` — tag `Strategy`.
- [ ] `src/core/Accounts/AccountService.cs` — tag `Account`.
- [ ] `src/core/Providers/*.cs` — tag `Provider`, provider name as structured field.
- [ ] `src/core/Overview/MonteCarloEngine.cs` — tag `MonteCarlo`.
- [ ] Any other `_log.Info` / `_log.Error` call site.

### Phase 10.12C — Config Integration
- [ ] `AppSettings.LogLevel` property (string: `"Info"`, `"Warn"`, `"Error"`, `"Debug"`). Auto-renders as a TextBox in the Config tab.
- [ ] Serilog `MinimumLevel` reads from `AppSettings.LogLevel` at startup.

### Constraints
- No allocation inside hot loops. Log once per completed operation, after timers stop, not inside read/write loops.
- No new NuGet package. Serilog is already referenced.
- File-per-run convention unchanged: `logs/runtime-{timestamp}.log` and `.json`.
- `data/strategies/*.cs` scripts that call `Log(string)` continue to work — `RunnerScriptGlobals.Log` maps to `LogManager.Info(LogTag.Strategy, message)`.

---

## Appendix A: Shipped Accounts

Format: ONE JSON per firm at `data/accounts/{Firm}.account.json`. Contains every plan and every size.

### FTMO (2 plans)
- **1-Step**: 10K, 25K, 50K, 100K, 200K. Profit Target 10%, Max Daily Loss 3%, Max Loss 10%, Best Day Rule 50%, Profit Split 90%, Trailing EOD drawdown.
- **2-Step**: 10K, 25K, 50K, 100K, 200K. Phase 1 10%, Phase 2 5%, Max Daily Loss 5%, Max Loss 10%, Min 4 trading days, Profit Split 90%, Static drawdown.

### Maventrading (7 plans)
- **Standard 1-Step**: 2K, 5K, 10K, 20K, 50K, 100K. Target 8%, MaxLoss 5%, DailyLoss 3%, Split 80%.
- **Standard 2-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1 8%, P2 5%, MaxLoss 8%, DailyLoss 4%, Split 80%.
- **Standard 3-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1/P2/P3 3% each, MaxLoss 3%, DailyLoss 2%, Split 80%.
- **Instant**: 2K, 5K, 10K, 20K, 50K, 100K. No target, MaxLoss 3%, DailyLoss 2%, Split 80%, Consistency Yes.
- **Mini**: 2K, 5K, 10K, 20K, 50K, 100K. No target, MaxLoss 3%, DailyLoss 2%, Split 70%, Payout 24h, Consistency Yes.
- **Omo 2-Step**: 2K, 5K, 10K, 20K, 50K, 100K. P1 6%, P2 8%, MaxLoss 8%, DailyLoss 4%, Split 80%.
- **Buy Now Pay Later**: 2K, 5K, 10K, 20K, 50K, 100K. P1 4%, P1 MaxLoss 10%, Funded MaxLoss 8%, Funded DailyLoss 4%, Split 80%, Funded Consistency 20%.

### Template
- `_TEMPLATE.account.json` — documents every field with placeholders. Copy to add a new firm. Rename to `.account.json.txt` to exclude from the scan.