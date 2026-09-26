# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

### Phase 10.15D -- MonteCarloViewModel rework [NEXT]

### Phase 10.15A -- Trade extension [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.15B -- TimeSlice + builder [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.15C -- New MonteCarloEngine [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.15D -- MonteCarloViewModel rework
- [ ] Read active account rules from `AccountService.Active`. Last phase's `MaxLossPercent`, `MaxDailyLossPercent`, `ProfitTargetPercent`, `DrawdownType`.
- [ ] Build `TimeSlice[]` via `TimeSliceBuilder.Build(data.Trades, data.SourceData)`.
- [ ] Independent run per queued dataset when `LastRunStore.Batch.Count > 1`.
- [ ] Display `P(ruin)`, `P(funded)`, `P(timeout)` in the summary panel — replace `Risk of Ruin`.
- [ ] Replace `Net Profit Distribution` histogram with terminal-balance distribution colored by outcome (blown, passed, timeout).
- [ ] Fix `Annual Return` and `Sharpe` computations: use wall-clock duration from `SourceData` span, not `result.Duration`.
- [ ] Fix histogram rendering: `plt.Axes.AutoScale()` after adding bars, then add percentile lines.

### Phase 10.15E -- Overview batch summary
- [ ] `OverviewViewModel` reads `LastRunStore.Batch` and displays per-dataset outcomes.
- [ ] Aggregate metrics computed by summing expectations, not pooling trades.

---

## Phase 10.14: Selection Queue [IN PROGRESS]

### Phase 10.14A -- Queue model + Data tab [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.14B -- Runner batch execution [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.14C -- Chart consumption [COMPLETED]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.14D -- Monte Carlo + Overview batch awareness [IN PROGRESS]
Being absorbed into Phase 10.15D/E.

---

## Phase 10.11C2: Equity Curve Rendering [PENDING]
- [ ] Extend Monte Carlo result to store per-simulation equity curves.
- [ ] Spaghetti plot on the Monte Carlo tab.

---

## Phase 10.7: Chart Tab [IN PROGRESS]
See `docs/ROADMAP_ARCHIVE.md`.

### Phase 10.7C -- Timeframe Switching [PENDING]
### Phase 10.7D -- Crosshair & Readout [PENDING]
### Phase 10.7E -- Polish [IN PROGRESS]

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