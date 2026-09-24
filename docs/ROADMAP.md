# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.10: Account Configuration [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` — 10.10A (Account Model & Service) and 10.10B (Accounts Tab GUI) shipped.

## Phase 10.11: Monte Carlo [IN PROGRESS]

Stationary block bootstrap over the last strategy run's trades. Consumes `LastRunStore` (shipped) and `AccountService.Active` (shipped).

### Phase 10.11A -- LastRunStore & OverviewData [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11B -- Monte Carlo Core
- [ ] `MonteCarloEngine` in `src/core/Overview/`.
- [ ] Stationary block bootstrap: block length drawn from geometric distribution with mean `b`.
- [ ] `Random.Shared` for thread-safe parallel simulation. No NuGet RNG package.
- [ ] `Parallel.For` with pre-allocated `double[]` result arrays. No `List<T>.Add`, no LINQ in hot loop.
- [ ] Per-simulation metrics: Net Profit, Max Drawdown %, Max Consecutive Losses.
- [ ] Risk of Ruin: fraction of simulations where equity drops below `StartingCapital * (1 - MaxLossPercent/100)`. Reads MaxLossPercent from `AccountService.Active.Phases[last].MaxLossPercent`. Falls back to 50% if no active account.
- [ ] Optional fixed seed for reproducibility.
- [ ] `IProgress<int>` for UI progress.
- [ ] `MonteCarloResult` record: `double[] NetProfits`, `double[] MaxDrawdowns`, `int[] MaxConsecutiveLosses`, `double RiskOfRuin`, percentile aggregates, metadata.

### Phase 10.11C -- Monte Carlo Tab GUI
- [ ] New **Monte Carlo** tab in MainWindow.axaml, positioned between Runner and Metrics.
- [ ] Toolbar: Iterations (default 1000), Mean Block Size (default 5), Ruin Threshold % (defaults to active account's funded MaxLossPercent), Progress Interval % (default 10), Run MC button, ProgressBar.
- [ ] Left panel: Summary stats — Net Profit (Mean/Median/StdDev/p05/p95), Max Drawdown (same), Max Consecutive Losses, **Risk of Ruin** prominent.
- [ ] Right top: Equity curve plot (ScottPlot). Base + p05 + p95 + straw broom count (default 20, configurable).
- [ ] Right bottom: Net Profit histogram with vertical lines at p05/p50/p95.
- [ ] Active account name displayed in the toolbar.
- [ ] Reads `LastRunStore.Current` trades + `AccountService.Active` rules.

### Phase 10.11D -- Overview Tab (Full)
- [ ] Full in-sample metrics table: CAGR, Expectancy, Sharpe, Sortino, Profit Factor, Win Rate, Avg Duration, Max DD.
- [ ] Monte Carlo summary: Risk of Ruin, p05/p50/p95 for Net Profit and Max DD.
- [ ] Three-column header (In-Sample / Monte Carlo / Out-of-Sample — OOS deferred).
- [ ] Color-coded cells.

### Dependencies
- 10.11A before 10.11B and 10.11C.
- 10.11B before 10.11C.
- 10.11C before 10.11D (Overview reads MC results).

---

## Phase 10.7: Chart Tab [IN PROGRESS]

See `docs/ROADMAP_ARCHIVE.md` for completed sub-phases (10.7A, 10.7B, 10.7F).

### Phase 10.7C -- Timeframe Switching [PENDING]
- [ ] Timeframe picker: `1m`, `5m`, `15m`, `1h`, `4h`, `1D`, `1W`.
- [ ] Fetch per-timeframe from provider, cache under `symbol@timeframe`.

### Phase 10.7D -- Crosshair & Readout [PENDING]
- [ ] Mouse crosshair tracking on the price panel.
- [ ] O/H/L/C/Volume/timestamp readout box.

### Phase 10.7E -- Polish [IN PROGRESS]
- [ ] Save/restore last symbol + timeframe.
- [ ] Empty state, error state, selectable error texts.
- [ ] Lock price-to-bar ratio.

---

## Phase 12: Hardening & Housekeeping [PLANNED]

---

## Appendix A: Shipped Accounts

### FTMO (USD) — 1-Step
Sizes: 10K, 25K, 50K, 100K, 200K. Profit Target 10%, Max Daily Loss 3%, Max Loss 10%, Best Day Rule 50%, Profit Split 90%.
Shipped: 100K. Add the remaining sizes via the Accounts tab.

### FTMO (USD) — 2-Step
Sizes: 10K, 25K, 50K, 100K, 200K. Phase 1 Target 10%, Phase 2 Target 5%, Max Daily Loss 5%, Max Loss 10%, Min Trading Days 4, Profit Split up to 90%.
Shipped: 100K. Add the remaining sizes via the Accounts tab.

### Maventrading — Standard 1-Step
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. Profit Target 8%, Max Loss 5%, Daily Loss 3%, Profit Split 80%, Consistency No.

### Maventrading — Standard 2-Step
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. Phase 1 Target 8%, Phase 2 Target 5%, Max Loss 8%, Daily Loss 4%, Profit Split 80%.
Shipped: 50K. Add the remaining sizes via the Accounts tab.

### Maventrading — Standard 3-Step
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. Phase 1/2/3 Target 3% each, Max Loss 3%, Daily Loss 2%, Profit Split 80%.

### Maventrading — Instant
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. No target, Max Loss 3%, Daily Loss 2%, Profit Split 80%, Consistency Yes.

### Maventrading — Mini
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. No target, Max Loss 3%, Daily Loss 2%, Profit Split 70%, Payout 24h, Consistency Yes.

### Maventrading — Omo 2-Step
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. Phase 1 Target 6%, Phase 2 Target 8%, Max Loss 8%, Daily Loss 4%, Profit Split 80%.

### Maventrading — Buy Now Pay Later
Sizes: 2K, 5K, 10K, 20K, 50K, 100K. Phase 1 Target 4%, Phase 1 Max Loss 10%, Funded Max Loss 8%, Funded Daily Loss 4%, Profit Split 80%, Funded Consistency 20%.