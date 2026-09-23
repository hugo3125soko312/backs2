# Roadmap: backs2 Trading Engine

Active work only. Completed phases are in `docs/ROADMAP_ARCHIVE.md`.

---

## Phase 10.7: Chart Tab [IN PROGRESS]

Chart tab is functional with OxyPlot.Avalonia 2.1.0-Avalonia11. Foundation (10.7A) and volume (10.7B) are complete — see `docs/ROADMAP_ARCHIVE.md`. Remaining sub-phases below.


### Phase 10.7D -- Crosshair & Readout [PENDING]
- [ ] Mouse crosshair tracking on the price panel.
- [ ] Readout box showing O/H/L/C/Volume/timestamp for the hovered bar.
- [ ] Optional: snap crosshair to nearest bar.
- [ ] **Lock price to bar ratio** (deferred from 10.7A) -- enforce a fixed pixels-per-price-unit ratio during pan/zoom via custom pointer-event handling on `PlotController`.

### Phase 10.7E -- Polish [IN PROGRESS]
- [ ] Save/restore last-viewed symbol + timeframe between app runs (write to `config/settings.json` via `ConfigManager`).
- [ ] Empty state: if no symbol selected or no data, show a friendly message instead of a blank canvas.
- [ ] Error state: if provider fetch fails, show the fallback chain's progress messages in a status line.
- [ ] **Selectable error texts.** Status line and error messages must use `SelectableTextBlock` instead of `TextBox` or `TextBlock`, so users can select and copy exception text.
- [x] **Data tab empty state bug.** Resolved. Root cause: the `DataGrid` had no resolvable `ControlTheme` when hosted inside the `TabControl` — the application-level `StyleInclude` in `App.axaml` was present but not taking effect on the grid's own resource host. Fix: added a scoped `<UserControl.Styles>` `StyleInclude` for `avares://Avalonia.Controls.DataGrid/Themes/Fluent.xaml` inside `DataView.axaml`.

### Phase 10.7F -- SkiaSharp Render Path [BLOCKED]

Status: **BLOCKED — no released NuGet package for the SkiaSharp PlotView.**

### Problem
Candlestick bodies and wicks render with soft/aliased edges against the dark background. Tested on two monitors with different DPI: the issue persists on both and does not resolve after moving the window between them, so it is not a per-monitor DPI scaling problem. The pan/zoom interaction is also slower than expected.

### Root Cause
The `OxyPlot.Avalonia 2.1.0-Avalonia11` package renders through Avalonia's generic `DrawingContext` (vector) pipeline, not the SkiaSharp renderer. Sub-pixel softness on 1-pixel lines is a known limitation of that path, and OxyPlot re-renders the entire plot model on every invalidation, which drives the pan performance cost.

### What Has Been Tried
- [x] `RenderOptions.EdgeMode="Aliased"` on the `PlotView` -- sharpened candles but destroyed text anti-aliasing on every child primitive. Rejected.
- [x] `UseLayoutRounding="True"` on the `Border` and `PlotView` -- fixed layout alignment, does not affect series rasterization.
- [x] `TextOptions.TextRenderingMode` -- not resolvable in this Avalonia version; removed.
- [x] `OxyPlotModule.EnsureLoaded()` -- not applicable; it is only needed for the SkiaSharp package, which is not installed.

### Path Forward (pick one when resuming)
- [ ] **Option A — Adopt the SkiaSharp PlotView from the open PR.** Copy the `PlotRenderer` and `PlotView` classes from https://github.com/oxyplot/oxyplot-avalonia/pull/71 into the project. The double-buffered variant is the one that keeps the UI responsive during pan. Substantial work; phase-sized.
- [ ] **Option B — Wait for the upstream PR to merge.** Out of project control.
- [ ] **Option C — Accept the soft candles.** Not recommended, but functionally defensible for a chart that is one of several tabs.

### Dependencies
- Feeds into Phase 11 (Monte Carlo) if equity curves are to be plotted with the same stack.

### Chart Tab — General Dependencies
- Depends on Phase 10 (DI wiring) -- done.
- Depends on Phase 10.5 (cache performance) -- done; chart fetches hit the same tiers.

---

## Phase 10.8: Metrics Library Expansion [PLANNED]

Scope: grow `MetricRegistry` from its current 3 metrics (SessionHigh, VWAP, ZScore) into a broad library. Classify every metric by **lag** (leading / lagging / mixed) and **usage** (trend / momentum / volatility / volume / breadth). UI exposes filtering by these categories.

### Phase 10.8A -- Metric Taxonomy
- [ ] Extend `IMetric` with metadata:

    public enum MetricLag { Leading, Lagging, Mixed }
    public enum MetricCategory { Trend, Momentum, Volatility, Volume, Support, Breadth, Statistical }

    public interface IMetric
    {
        string Name { get; }
        MetricLag Lag { get; }
        MetricCategory Category { get; }
        string Description { get; }
        double Calculate(List<Kline> data);
    }

---

## Phase 11: Monte Carlo & Performance Analytics [PLANNED]

Target: reproduce the TradingView / Fractalyst-style statistics panel -- In-Sample Analysis, Monte Carlo Analysis, Out-of-Sample Analysis, plus full Performance / Trade / Risk metrics.

### Phase 11A -- Backtest Result Model
- [ ] `BacktestResult` record: equity curve, trade list, per-trade PnL, holding durations, win/loss counts.
- [ ] `Trade` record: entry time, exit time, entry price, exit price, direction, size, PnL, PnL%, MAE, MFE.
- [ ] `StrategyManager.RunBacktestFromFileAsync` returns a populated `BacktestResult` (via `ScriptGlobals.Result`). Scripts declare a strategy type or register trades explicitly.

### Phase 11B -- In-Sample Metrics
- [ ] CAGR (Compound Annual Growth Rate).
- [ ] Expectancy (average PnL per trade in currency and %).
- [ ] Max Drawdown (absolute and %).
- [ ] Sortino ratio.
- [ ] Net Profit, Profit Factor, Payoff ratio.
- [ ] Sample Size (trade count).
- [ ] Avg Duration, Max Consecutive Wins (Max CW), Max Consecutive Losses (Max CL).
- [ ] Win rate, loss rate, breakeven rate.
- [ ] Frequency (trades per week/month), Exposure (% time in market).
- [ ] Sharpe ratio, Sortino ratio.
- [ ] MAR, RoR, R2, CVaR, p-value.
- [ ] Alpha, Beta (vs Buy & Hold).
- [ ] Compounding, Avg Win, Avg Trade, Avg Loss.
- [ ] Rolling Expectancy, Avg Win Duration (Avg W Dur), Avg Loss Duration (Avg L Dur).
- [ ] Max Equity (Max Eq), Min Equity (Min Eq), Rolling Sortino.
- [ ] Current Drawdown (Curr DD), DAR, Kurtosis, Skewness, VaR, Ulcer index.

### Phase 11C -- Monte Carlo Simulation
- [ ] Bootstrap resampling of trade sequence (with and without replacement).
- [ ] Randomized trade ordering (shuffle).
- [ ] Optional: randomized entry/exit jitter.
- [ ] Configurable iteration count (default 1000).
- [ ] Output distribution per metric (CAGR, Expectancy, Max DD, Sortino).
- [ ] Report: Best / Median / Worst / Mean / StdDev per metric.
- [ ] Confidence intervals (5% / 95%).
- [ ] Histogram rendering (optional phase 11E).

### Phase 11D -- Out-of-Sample Analysis
- [ ] Split the data: In-Sample vs Out-of-Sample windows (ratio configurable, default 70/30).
- [ ] Run identical metrics on both windows.
- [ ] Report side-by-side comparison.
- [ ] Walk-forward analysis (rolling window) -- optional sub-phase.

### Phase 11E -- GUI
- [ ] New **Analytics** tab (or a pane inside Strategies).
- [ ] Table matching the TradingView layout: three-column header (In-Sample / Monte Carlo / Out-of-Sample).
- [ ] Row labels from Phase 11B/11C.
- [ ] Color-coded cells (green positive, red negative) matching the reference.
- [ ] Export results to CSV / JSON for external review.

### Phase 11F -- Strategy Authoring Contract
- [ ] Define how a strategy declares trades. Options:
  - **A)** Strategy returns a `List<Trade>` explicitly.
  - **B)** `ScriptGlobals` exposes `Enter(side, price)`, `Exit(price)`, `Size`, etc. Engine tracks positions.
  - **C)** Strategy returns a signal series (long/short/flat per bar); engine derives trades.
- [ ] Decision needed before Phase 11A is implemented.

### Dependencies
- Depends on Phase 8 (symbol selection) -- Monte Carlo runs require a chosen instrument and date range.
- Depends on Phase 10.7 chart stack if histograms/equity curves are to be plotted.
- Package candidates: `MathNet.Numerics` (statistics), `OxyPlot.Avalonia` (already present, for histograms/equity).

---

## Phase 12: Hardening & Housekeeping [PLANNED]