# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.10: Account Configuration [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

## Phase 10.11: Monte Carlo [IN PROGRESS]

### Phase 10.11A -- LastRunStore & OverviewData [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11B -- Monte Carlo Core [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11C -- Monte Carlo Tab GUI [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.11C2 -- Equity Curve Rendering [PENDING]
- [ ] Extend `MonteCarloEngine` to store per-simulation equity curves (`double[][]`).
- [ ] Spaghetti plot: base, p05, p95 + configurable straw broom.
- [ ] Render only fixed lines + straw broom sample, never all 1000.

### Phase 10.11D -- Overview Tab (Full)
- [ ] Full in-sample metrics table: CAGR, Expectancy, Sharpe, Sortino, Profit Factor, Win Rate, Avg Duration, Max DD.
- [ ] Monte Carlo summary: Risk of Ruin, p05/p50/p95 for Net Profit and Max DD.
- [ ] Three-column header (In-Sample / Monte Carlo / Out-of-Sample).
- [ ] Color-coded cells.

---

## Phase 10.12: Tagged Logging Refactor [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.13: VWAP Zero-Volume Fallback [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.14: Selection Queue [IN PROGRESS]

The Data tab is the single source of truth for "which datasets are enabled." Consumer tabs read from the queue instead of maintaining their own pickers.

### Phase 10.14A -- Queue Model + Data Tab [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.14B -- Runner Batch Execution [PENDING]
- [ ] `RunnerViewModel` reads the queue instead of its own symbol/timeframe ComboBoxes.
- [ ] `RunAsync` iterates every queued dataset, runs the strategy against each, and produces N `StrategyRunResult`s.
- [ ] Before running with >1 queued dataset, show a modal confirmation: "You have selected N datasets. Running will execute the strategy on all N datasets sequentially. [Yes, continue] [Cancel]."
- [ ] `LastRunStore.Publish` extended to hold a batch — either `IReadOnlyList<OverviewData>` or a single `Current` plus a `Batch` list.
- [ ] Empty queue: Run button disabled, hint shows "Add data in the Data tab first."
- [ ] Runner's own symbol/timeframe ComboBoxes removed.

### Phase 10.14C -- Chart Consumption [PENDING]
- [ ] `ChartViewModel` reads the queue.
- [ ] Chart has a small dropdown at the top to pick which queued dataset to display.
- [ ] Chart's own symbol/timeframe ComboBoxes removed.

### Phase 10.14D -- Monte Carlo + Overview Batch Awareness [PENDING]
- [ ] Monte Carlo tab gains a dataset picker when `LastRunStore.Batch` has more than one entry.
- [ ] Overview tab does the same.
- [ ] If `LastRunStore.Batch` has exactly one entry, no picker is shown — behavior unchanged.

### Dependencies
- 10.14A before 10.14B and 10.14C.
- 10.14B before 10.14D.

---

## Phase 10.7: Chart Tab [IN PROGRESS]
See `docs/ROADMAP_ARCHIVE.md` for completed sub-phases.

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

## Phase 12: Hardening & Housekeeping [PLANNED]

---

## Appendix A: Shipped Accounts

Format: ONE JSON per firm at `data/accounts/{Firm}.account.json`.

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
- `_TEMPLATE.account.json` — schema reference. Copy to add a new firm. Rename to `.account.json.txt` to exclude from the scan.