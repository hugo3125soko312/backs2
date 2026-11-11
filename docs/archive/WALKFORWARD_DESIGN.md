# Walk-Forward Optimization — Design Doc

**Status:** in progress (batch 1 of 3 landed)
**Author:** implementation
**Created:** 2026-11-05

## 1. Overview

Walk-forward optimization (WF) extends the Runner tab with a third execution
mode beyond single backtest and grid optimize. Per window, we optimize on an
in-sample (IS) slice, pick the winning parameter combination, evaluate it on
the following out-of-sample (OOS) slice that the optimizer never saw, and
aggregate the OOS results. The point is to measure generalization, not just
fit — the difference between IS and OOS objective is the degradation signal.

Two user-visible additions:

1. A **Walk-Forward Settings** expander on the existing Runner subtab.
2. A new **Walk-Forward Results** subtab (Runner index 2), parallel to the
   existing Optimization Results subtab.

## 2. What this touches, and what it deliberately does not

**New code:**

- `src/core/WalkForward/WalkForwardModels.cs`         — records + constants
- `src/core/WalkForward/WalkForwardWindowPlanner.cs`  — pure planner
- `src/core/WalkForward/WalkForwardObjective.cs`      — pure objective selector
- `src/core/WalkForward/WalkForwardAggregator.cs`     — pure summary + curve stitch
- `src/core/WalkForward/WalkForwardJsonContext.cs`    — source-gen JSON context
- `src/core/WalkForward/WalkForwardStore.cs`          — persistence under data/walkforward/
- `src/core/WalkForward/WalkForwardRunner.cs`         — orchestrator (batch 2)
- `src/gui/Runner/RunnerViewModel.WalkForward.cs`     — config + run command (batch 2)
- `src/gui/Runner/RunnerViewModel.WalkForwardResults.cs` — results subtab (batch 3)
- `src/gui/Runner/WalkForwardWindowRow.cs`            — DataGrid row projection (batch 3)
- `src/gui/Runner/WalkForwardBatchHeader.cs`          — batch picker item (batch 3)

**Modified code:**

- `src/core/ConfigManager.cs` — new AppSettings fields + normalizers (batch 2)
- `src/core/Optimizer/OptimizationRunner.cs` — optional `persist` flag on `RunAsync`
  (batch 2, additive only, default true so existing callers are unaffected)
- `src/gui/Runner/RunnerViewModel.cs` — ctor deps + subtab index 2 flag (batch 2)
- `src/gui/Runner/RunnerView.axaml` — WF expander + subtab-2 Grid (batch 3)
- `src/gui/MainViewModel.cs` — one new NavigationNode (batch 3)
- `App.axaml.cs` — two `AddSingleton` lines (batch 2)

**Deliberately not touched:**

- `OptimizationBatch`, `OptimizationStore`, `OptimizationJsonContext` — the
  existing optimizer archive format is frozen. WF has its own directory, its
  own store, its own schema version.
- The per-version hot path inside `OptimizationRunner.RunAsync`. **This is the
  single most important decision in the whole design**, see §6.
- The other 11 main tabs.

## 3. Data model

All records live in `src/core/WalkForward/WalkForwardModels.cs`. They are
`sealed record` types (immutable, JSON-friendly via source-gen, structural
equality for tests).

- `WalkForwardRunConfig` — user-supplied parameters.
  `IsLength`/`OosLength`/`StepLength` are in *whatever unit* `LengthUnit`
  names ("Bars" or "Percent"). `Optimization` is the reused
  `OptimizationRunConfig` — grid caps, MC iterations, DD rules, capital.
- `WalkForwardProgress` — one event per stage boundary and per window.
  `Fraction` is monotonic 0..1 across the whole run.
- `WalkForwardCandidate` — non-winning OOS evaluations, carried only when
  `CarryTopN > 1`.
- `WalkForwardWindowResult` — one per window, success or failure.
  `OosEquityCurve` is downsampled to ≤256 points, window-local (starts at 0).
- `WalkForwardSummary` — aggregation across successful windows only.
  `StitchedEquityCurve` is the concatenation of per-window curves with
  cumulative-PnL offsets, downsampled to ≤1024 points.
- `WalkForwardBatch` — the persisted root. `SchemaVersion` starts at 1.

## 4. Windowing

`WalkForwardWindowPlanner.Plan(totalBars, cfg)` is pure and testable. It:

1. Resolves lengths to bars (Percent => `round(total * pct / 100)`, min 1).
2. Validates: `isLen >= 100` (MIN_IS_BARS), `step >= oosLen` (no OOS overlap
   in the stitched PnL), `n >= MinWindows`.
3. Emits windows:
   - **Anchored**: `isStart = 0`, `isEnd = isLen + w * step`.
   - **Rolling**: `isStart = w * step`, `isEnd = isStart + isLen`.
   - In both cases `oosStart = isEnd`, `oosEnd = oosStart + oosLen`.
   - Stop when `oosEnd > totalBars`.
4. `Describe(...)` returns a short human-readable preview for the UI
   (e.g. `"7 windows · IS 6,000 bars · OOS 1,000"`), or a reason string when
   planning fails.

`WalkForwardPlanException` carries a user-facing message. The VM catches it
and surfaces it in `StatusText`, no crash, no partial run.

## 5. Objective selection

`WalkForwardObjective.SupportedKeys` are the strings the UI offers and the
planner accepts. `Select(key, OptimizationVersion v)` returns a
`double` where higher is better:

| Key | Field | Missing MC |
|---|---|---|
| `EdgePercent` | `v.Mc!.EdgePercent` | −∞ |
| `PFunded` | `v.Mc!.PFunded` | −∞ |
| `NetPnl` | `v.NetPnl` | (always present) |
| `PRuin` | `-v.Mc!.PRuin` (negated — lower ruin is better) | −∞ |
| `TerminalBalanceMedian` | `v.Mc!.TerminalBalanceMedian` | −∞ |

−∞ is deliberate: a version without MC is never selectable, never a
candidate. `SupportedKeys` is the single source of truth the config
normalizer validates against — adding a new key is a one-file change here.

## 6. Reusing the optimizer — the seam

The one decision that matters for "be cautious, this will grow":

**Batch 1 does not patch `OptimizationRunner`.** Instead, `WalkForwardRunner`
(in batch 2) will take the run/evaluate operations as **delegates**:

```csharp
// IS slice — full grid optimize
public delegate Task<OptimizationBatch> IsWindowRunner(
    string strategyPath, string strategyName, string symbol, string timeframe,
    List<Kline> data, IReadOnlyList<GridParameter> grid,
    OptimizationRunConfig config, IProgress<OptimizationProgress>? progress,
    CancellationToken ct);

// OOS slice — single combination, return one OptimizationVersion
public delegate Task<OptimizationVersion> OosWindowEvaluator(
    string strategyPath, string strategyName, string symbol, string timeframe,
    List<Kline> data, ParameterSet[] parameters,
    OptimizationRunConfig config, int mcIterations,
    CancellationToken ct);
```

Batch 2 wires them to two implementations:

- `IsWindowRunner` → `OptimizationRunner.RunAsync` with `persist: false`
  (the small additive change to `OptimizationRunner`, still no refactor of
  the hot path).
- `OosWindowEvaluator` → **v1 fallback:** one-combination `RunAsync`. This
  runs the full coarse+refine pipeline on a single combination; slower per
  window than a thin single-version backtest, but zero risk to the existing
  optimizer. **v2 (later):** swap the delegate for a real
  `OptimizationRunner.EvaluateVersionAsync` extracted from the
  per-version body. Because the delegate signature is already in place, the
  swap is one line in the DI wiring and does not touch the runner's
  public surface.

This is why `IsWindowRunner` / `OosWindowEvaluator` exist as explicit
delegates from day one. The cost of the seam is three lines of `using`;
the benefit is that the risky refactor is deferred, one-line reversible,
and testable against a fake implementation.

## 7. Aggregation

`WalkForwardAggregator.Aggregate(windows)`:

- **Means / medians** are taken over `Success == true` windows only.
- **`PctWindowsPositiveOosPnl`** = 100 × (successful windows with
  `OosNetPnl > 0` / successful).
- **`PctWindowsBeatNull`** = 100 × (successful windows with
  `OosBeatNull == true` / successful).
- **`StitchedOosNetPnl`** = sum of `OosNetPnl` over successful windows,
  in chronological order. Because the planner enforces `step >= oosLen`,
  OOS ranges never overlap, so the sum is a real cumulative PnL, not an
  average of overlapping windows.
- **`StitchedOosMaxDrawdown`** = peak-to-trough on the stitched equity
  curve; fallback is max of per-window DDs if any curve is empty (e.g. the
  MC pass dropped curves because `CurvesRetained == false`).
- **`ConsistencyScore`** = `(pctPositive/100) × (1 − min(1, stddev(OosNetPnl) / (|mean(OosNetPnl)| + ε)))`,
  clamped to 0..1, zero when < 2 successful windows. A high score means
  many positive windows **and** a tight distribution — it does not reward
  one lucky window that dominates.

The stitched curve is built by accumulating per-window cumulative PnL
(offset by all previous windows' net PnL), concatenated in window order, and
linearly downsampled to ≤1024 points.

## 8. Persistence

`WalkForwardStore` mirrors `OptimizationStore` 1:1:

- Directory: `data/walkforward/`.
- Filename: `wf_{Id}.json`, `Id = yyyyMMddTHHmmssfff-xxxx` (sort-friendly).
- `SaveAsync` writes to `wf_{Id}.json.tmp` and `File.Move(overwrite: true)`.
- `LoadAsync` returns null for missing files, empty files, or a
  `SchemaVersion` newer than `WalkForwardBatch.CurrentSchema` (forward-compat
  guard, warns to the log).
- `ListBatchFiles()` returns files newest-first by ordinal string compare
  on filename — which is chronological because the Id starts with a UTC
  timestamp.
- `NewId()` returns the current UTC timestamp + 4 random hex chars.

`WalkForwardJsonContext` is a `JsonSerializerContext` with
`NumberHandling = AllowNamedFloatingPointLiterals` so NaN/∞ survive a
round-trip (metrics can be NaN with zero trades). `McSummary` and
`ParameterSet` are pulled in transitively as members of `WalkForwardBatch`.
Camel-case naming, nulls omitted — same conventions as
`OptimizationJsonContext`.

Schema evolution policy: every new field after v1 must be nullable or have
a default, and any change that removes or retypes a field bumps
`CurrentSchema` and the loader rejects the newer file. This is enforced by
convention + one code path in `LoadAsync`, not by a migration system.

## 9. UI (batch 3)

**Config** — a new `Expander Header="Walk-Forward Settings"` inside the
Runner panel (which becomes a 2-row Grid: expander, then the existing
strategy-list + parameter-grid grid). Every control binds to a
`Wf*` property on `RunnerViewModel`. Backend ComboBox binds to the
**existing** `McBackend` — one source of truth for Auto/Cpu/Gpu, no
duplicate setting to silently disagree with. A `WfPlanPreviewText` shows
the planner's `Describe(...)` output live, red when the plan is invalid.

**Results subtab 2** — a `Grid RowDefinitions="Auto,Auto,*,Auto"`:
batch picker + filters + sort + cap + Refresh/Open Folder/Export CSV
toolbar, an 8-cell aggregate header, the window DataGrid, and a footer note
explaining degradation and the stitched-curve definition. Same
`RangeObservableCollection` + `Clear(); AddRange(...)` bulk-swap pattern as
Optimization Results to keep the DataGrid responsive.

**Navigation** — one new `NavigationNode` in `MainViewModel.BuildNavigationTree`:

```csharp
_runnerNode.Children.Add(new NavigationNode {
    Header = "Walk-Forward Results", SubtabIndex = 2, Factory = () => RunnerVm
});
```

`SelectedSubtabIndex == 2` drives a new `IsWalkForwardResultsPanelVisible`
flag, added to the existing `[NotifyPropertyChangedFor]` chain on
`SelectedSubtabIndex`.

## 10. Backend selection

WF does not own the backend choice. `IComputeDispatcher` re-reads
`MonteCarloBackend` per dispatch, so the user's choice of Auto/Cpu/Gpu on
the MC expander governs IS and OOS MC passes. Flipping the combo mid-run
affects the next dispatch, never one in flight. The batch's
`Config.Backend` records the value at run start — provenance only, with
this caveat documented in the UI tooltip.

## 11. Backward compatibility

- **Old settings.json**: every new `AppSettings` field has an initializer;
  `System.Text.Json` leaves absent properties at their initializer values.
  Normalizers run post-load and fall back on unrecognized values.
- **Old optimization batches**: untouched. `OptimizationStore` and
  `OptimizationJsonContext` are not modified. `OptimizationRunner.RunAsync`
  gains a trailing optional `bool persist = true` — every existing caller
  compiles and behaves identically.
- **Old WF batches after a schema bump**: rejected with a warning, never
  silently mis-parsed.

## 12. Testing strategy

Unit tests target the pure layers first (batch 1):

1. `Planner_Anchored` — fixed bars, expected window sequence.
2. `Planner_Rolling` — same, isStart advances.
3. `Planner_RejectsTooFewWindows` — message includes needed/available.
4. `Planner_RejectsStepLessThanOos` and `IsLength < MIN_IS_BARS`.
5. `Planner_PercentVsBars` — same plan from both units.
6. `Objective_Select` — each key, `PRuin` negation, missing MC → −∞.
7. `Aggregator_Fixture` — hand-computed 4-window summary; stitched PnL/DD.
8. `Store_RoundTrip` — NaN preserved, ordering, schema-version guard.
9. `Settings_OldFileLoads` — missing WF keys fall back to defaults.

Integration (batches 2–3):

10. `Runner_Integration_FakeOptimizer` — delegates injected; assert OOS is
    evaluated on the OOS slice, not on the IS slice.
11. `Runner_Cancel` — cancel after window 2 → batch `Cancelled = true`.
12. `Runner_NoPollution` — after a WF run, `data/optimizations/` has no
    new files.

## 13. Open questions deferred to v1.1

- **OOS indicator warm-up.** Prepending N IS-tail bars to the OOS slice
  removes warm-up bias but requires a runner that can filter trades by
  entry time. Deferred; the current behaviour is documented as a known
  bias in the results footer.
- **Multi-dataset WF in one run.** v1 mirrors `OptimizeAsync` — one batch
  per queued dataset, sequentially. Batching across datasets would need
  a different aggregate axis.
- **Stitched equity chart on the results subtab.** Data is already in
  `Summary.StitchedEquityCurve`; only the chart is deferred.
- **Parameter stability small-multiples.** Data is in
  `ChosenParameters`; only the visualisation is deferred.

## 14. Changelog

- **2026-11-05** — batch 1: design doc + `WalkForwardModels`,
  `WalkForwardWindowPlanner`, `WalkForwardObjective`,
  `WalkForwardAggregator`, `WalkForwardJsonContext`, `WalkForwardStore`.
  No production call sites yet; no UI changes. Everything in this batch is
  self-contained and unit-testable.
---

## 15. Changelog addendum — 2026-11-06

Static review of batch 1 (compile still blocked by exec-bridge, see
`docs/execbridgereport1-3.md`):

- Brace balance: all six files matched (12/12, 1/1, 3/3, 6/6, 34/34, 26/26).
- CS APIs used: `Random.Shared` (.NET 6+), `Convert.ToHexString` (.NET 5+),
  `Array.Empty`, `MidpointRounding.AwayFromZero`, `AllowNamedFloatingPointLiterals`
  — all legal on `net8.0` (the project TFM, confirmed from `backs2.csproj`).
- No compilation performed. Batch 1 remains unverified until the sandbox
  permits Roslyn's Csc task to run (see report #3 §4).
