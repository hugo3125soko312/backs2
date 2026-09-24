# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.9: Runner & Presets [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md`. The Strategies tab is gone; the Runner tab replaces it. Preset save/load, parameter parsing, and per-parameter dirty state all ship.

## Phase 10.10: Overview Tab [IN PROGRESS]

First tab from the left. Reads the last `StrategyRunResult` (via `RunnerViewModel.RunCompleted`) and displays performance analytics + Monte Carlo distributions.

### Phase 10.10A -- Overview Data Model
- [ ] `LastRunStore` singleton holding the most recent `StrategyRunResult` + the strategy name, symbol, and timeframe it was run against.
- [ ] `RunnerViewModel.RunCompleted` writes into `LastRunStore`.
- [ ] `OverviewViewModel` subscribes to a `LastRunStore.Changed` event.

### Phase 10.10B -- In-Sample Metrics
- [ ] CAGR, Expectancy, Max Drawdown (abs + %), Sortino, Net Profit, Profit Factor, Payoff Ratio.
- [ ] Sample Size, Avg Duration, Max Consecutive Wins/Losses.
- [ ] Win/Loss/Breakeven rate, Frequency, Exposure.
- [ ] Sharpe, MAR, RoR, R2, CVaR, p-value, Alpha, Beta.
- [ ] Compounding, Avg Win/Trade/Loss, Rolling Expectancy, Avg Win/Loss Duration.
- [ ] Max/Min Equity, Rolling Sortino, Current Drawdown, DAR, Kurtosis, Skewness, VaR, Ulcer Index.

### Phase 10.10C -- Monte Carlo Simulation
- [ ] Bootstrap resampling of trade sequence (with and without replacement).
- [ ] Randomized trade ordering (shuffle).
- [ ] Optional: randomized entry/exit jitter.
- [ ] Configurable iteration count (default 1000).
- [ ] `Parallel.For` with `ThreadLocal<Random>`; no shared collection writes in the hot loop; `double[]` result arrays pre-allocated per iteration.
- [ ] Per-metric distribution: Best / Median / Worst / Mean / StdDev.
- [ ] Confidence intervals (5% / 95%).
- [ ] Risk-of-Ruin: fraction of simulations where equity drops below a configurable threshold.

### Phase 10.10D -- Out-of-Sample Analysis
- [ ] 70/30 in-sample / out-of-sample split (configurable).
- [ ] Run identical metrics on both windows.
- [ ] Side-by-side comparison.

### Phase 10.10E -- Overview GUI
- [ ] New Overview tab, first from the left in MainWindow.axaml.
- [ ] Equity curve (ScottPlot.Avalonia).
- [ ] Monte Carlo histogram per metric.
- [ ] Three-column header (In-Sample / Monte Carlo / Out-of-Sample).
- [ ] Color-coded cells.
- [ ] Export results to CSV / JSON.

### Dependencies
- Depends on Phase 10.9 (Runner + TradeRecorder) — done.
- Package candidates: `MathNet.Numerics` (statistics).

---

## Phase 10.7: Chart Tab [IN PROGRESS]

Foundation (10.7A), volume (10.7B), and SkiaSharp Render Path (10.7F) are complete — see `docs/ROADMAP_ARCHIVE.md`. Remaining sub-phases below.

### Phase 10.7C -- Timeframe Switching [PENDING]
- [ ] Timeframe picker: `1m`, `5m`, `15m`, `1h`, `4h`, `1D`, `1W`.
- [ ] **Data source decision:** fetch per-timeframe from provider, cache each timeframe under a distinct symbol key (e.g. `XAUUSD@1h`), so `DataManager` handles it as just another series.
- [ ] Progressbar during timeframe switch: `[ObservableProperty] bool IsLoading`, `[ObservableProperty] double LoadingProgress`.

### Phase 10.7D -- Crosshair & Readout [PENDING]
- [ ] Mouse crosshair tracking on the price panel.
- [ ] Readout box showing O/H/L/C/Volume/timestamp for the hovered bar.
- [ ] Optional: snap crosshair to nearest bar.

### Phase 10.7E -- Polish [IN PROGRESS]
- [ ] Save/restore last-viewed symbol + timeframe between app runs.
- [ ] Empty state: friendly message instead of a blank canvas.
- [ ] Error state: fallback chain's progress messages in a status line.
- [ ] Selectable error texts (`SelectableTextBlock`).
- [x] **Data tab empty state bug.** Resolved via scoped `<UserControl.Styles>` `StyleInclude` in `DataView.axaml`.
- [ ] **Lock price to bar ratio** (deferred from 10.7A, re-deferred from 10.7D).

### Chart Tab — General Dependencies
- Depends on Phase 10 (DI wiring) — done.
- Depends on Phase 10.5 (cache performance) — done.

---

## Phase 10.8: Metrics Library Expansion [COMPLETED]

See `docs/ROADMAP_ARCHIVE.md` — Phase 10.8A (Metric Taxonomy) and Phase 10.8B (Metric Suite Expansion) shipped.

## Phase 11: Monte Carlo & Performance Analytics [MERGED INTO 10.10]

The work previously planned under Phase 11A-11F is now Phase 10.10A-10.10E, above, because the Overview tab is the delivery vehicle. Trade model + authoring contract shipped in Phase 10.9.

## Phase 12: Hardening & Housekeeping [PLANNED]