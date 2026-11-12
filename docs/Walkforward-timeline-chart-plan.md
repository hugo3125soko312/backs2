# Plan: Replace Walk-Forward Results View with a Window Timeline Chart

## 1. Goal

Delete the current results area (wide DataGrid, plus the card view that was added) and replace it with the standard walk-forward diagram (your second image): one horizontal row per window, an **In-sample** bar followed by an **Out-of-sample** bar, stepping forward along a real date axis.

The current grid has about 19 columns, so it scrolls sideways and shows no time structure. The staircase chart shows the window layout at a glance, and you can colour it with results.

Improvements over the reference picture (all optional, listed in Section 7):

- Colour each OOS bar by result (green, amber, red) instead of one fixed navy.
- Print the OOS Net PnL and degradation on or beside each bar.
- Add a stitched OOS equity curve below the staircase, sharing the same date axis.
- Hover for details, click a window to open its row.

## 2. What exists today (from the current-state file)

- Settings expander (window mode Anchored or Rolling, length unit Percent or Bars, IS 60, OOS 10, Step 10, Min Windows 3, Objective, Carry Top N, plan preview).
- Results panel: sort-by dropdown, Cap box (200), Refresh, Open Folder, Clear, a DataGrid, status bar.
- Card view toggle with WfGood / WfWarn / WfBad thresholds (robustness 0.70).
- Charts in the app already use **ScottPlot.Avalonia** (`AvaPlot`, `ApplyPlotTheme()`, pointer handling in code-behind). The new chart should reuse that stack. No new package is needed.

## 3. Decision: what to delete, what to keep

### Delete

| Item | Where |
|---|---|
| Results DataGrid and its 19 column definitions | `RunnerView.axaml` results panel |
| Card view (template, styles, toggle) | `RunnerView.axaml`, `WalkForwardWindowCard.cs` |
| Card-only ViewModel members: `WalkForwardCards`, `WfShowCards`, `RebuildWalkForwardCards()`, verdict and summary text | `RunnerViewModel.WalkForwardResults.cs` |
| Card-only resources in `App.axaml` | only if nothing else uses them (check with grep) |
| Sort-by dropdown | keep only if the chart ordering needs it; otherwise remove |

### Keep (the chart still needs these)

- `WalkForwardStore` and the batch loading from `data/walkforward/*.json`
- `WalkForwardBatch`, `WalkForwardWindow`, `WalkForwardWindowRow` models
- `RefreshWalkForwardResultsAsync()`, Open Folder, Clear, status text
- The Walk-Forward settings expander and plan preview
- The Run and Cancel buttons

Before deleting anything: commit or tag the current state so it can be restored.

```bash
git add -A && git commit -m "snapshot before replacing walk-forward results view"
git grep -n "WalkForwardCards\|WfShowCards\|WalkForwardWindowCard" src/
```

## 4. Data needed per window

Each plotted row needs these values. Confirm each one exists in `WalkForwardWindowRow` or the saved window model. Most are already shown as grid columns (IS Start, IS End, OOS Start, OOS End, Window, Objective, OOS Objective, Degradation, OOS Net PnL, OOS Max DD, OOS Trades).

| Field | Use |
|---|---|
| `WindowIndex` | row position (W1 at the top) |
| `IsStart`, `IsEnd` | In-sample bar x-range |
| `OosStart`, `OosEnd` | Out-of-sample bar x-range |
| `OosNetPnl` | label and colour |
| `Degradation` | colour thresholds (below 0.25 good, 0.25 to 0.50 warn, above 0.50 or negative PnL bad) |
| `OosMaxDrawdown`, `OosTradeCount` | tooltip |
| `IsObjective`, `OosObjective` | tooltip |
| (optional) OOS equity points | stitched equity curve |

If the dates are stored as bar indices (Bars mode), convert them to `DateTime` using the dataset's bar timestamps when building the chart, so the x-axis shows real dates like the reference image.

Batch selection: the chart shows one batch at a time. Reuse the existing batch selector (`WalkForwardBatchIds`). Show the newest by default.

## 5. New component

### 5.1 Files

| New file | Purpose |
|---|---|
| `src/gui/Runner/WalkForwardTimelineView.axaml` | `UserControl` containing one `AvaPlot` (and optionally a second for the equity curve) |
| `src/gui/Runner/WalkForwardTimelineView.axaml.cs` | draws the chart, handles hover and click |
| `src/gui/Runner/WalkForwardTimelineBuilder.cs` | pure function: rows in, plot-ready shapes out (testable, no UI) |

The user control takes the `WalkForwardWindowRows` collection (already in the ViewModel) and redraws when it changes.

### 5.2 Drawing approach (ScottPlot 5)

```csharp
public void Render(IReadOnlyList<WalkForwardWindowRow> rows)
{
    Plot.Plot.Clear();
    var plt = Plot.Plot;

    for (int i = 0; i < rows.Count; i++)
    {
        var r = rows[i];
        double y = rows.Count - i;           // W1 on top
        const double h = 0.35;               // half bar height

        // In-sample bar
        var isBar = plt.Add.Rectangle(
            r.IsStart.ToOADate(), r.IsEnd.ToOADate(), y - h, y + h);
        isBar.FillColor   = ScottPlot.Color.FromHex("#E0C080");   // sand, as in the reference
        isBar.LineColor   = ScottPlot.Color.FromHex("#444444");

        // Out-of-sample bar, coloured by result
        var oosBar = plt.Add.Rectangle(
            r.OosStart.ToOADate(), r.OosEnd.ToOADate(), y - h, y + h);
        oosBar.FillColor  = HealthColor(r);                       // green / amber / red
        oosBar.LineColor  = ScottPlot.Color.FromHex("#444444");

        // Labels
        var isLabel = plt.Add.Text("In-sample", r.IsStart.ToOADate(), y);
        var oosLabel = plt.Add.Text($"OOS {r.OosNetPnl:+0;-0}", r.OosStart.ToOADate(), y);
    }

    plt.Axes.DateTimeTicksBottom();
    plt.Axes.Left.TickGenerator = BuildWindowTicks(rows);   // "W1", "W2", ... via ManualTickGenerator
    plt.Axes.Margins(left: 0.02, right: 0.02, bottom: 0.05, top: 0.05);
    plt.Grid.IsVisible = false;
    ApplyPlotTheme(Plot);                                    // existing dark theme helper
    Plot.Refresh();
}
```

Notes:

- ScottPlot 5 API names differ slightly between minor versions (`Add.Rectangle`, `Add.Text`, `Axes.DateTimeTicksBottom`). Check the installed version and adjust the names. The structure stays the same.
- If `Add.Rectangle` overloads differ, use `Add.Polygon` or a `Coordinates` rect with the same four numbers.
- Disable user zoom or pan if the chart should be static: use the same pointer-disabled pattern as the Monte Carlo plots. Or keep pan and zoom and add the arrow-key handling from the Chart tab.

### 5.3 Anchored vs Rolling

The same drawing code covers both:

- **Rolling** (the reference image): every IS bar has the same length and slides forward.
- **Anchored**: `IsStart` is the same for every window and the IS bar grows. The staircase becomes a growing bar with a sliding OOS bar. No special code, since the bar edges come from the data.

### 5.4 Colour rules

| Condition | Colour |
|---|---|
| OOS Net PnL < 0 or degradation > 0.50 | red |
| degradation between 0.25 and 0.50 | amber |
| otherwise | green |

Put the thresholds in one place (a small static class or settings) and reuse the existing `WfGood`, `WfWarn`, `WfBad` brushes so the colours match the rest of the app. Add a small legend (In-sample, OOS good, OOS warn, OOS bad).

## 6. Layout change in `RunnerView.axaml`

Results panel after the change:

```
[Sort/ordering dropdown if kept] [Cap] [Refresh] [Open Folder...] [Clear]   [Batch: v]
+----------------------------------------------------------------------+
| Summary line: windows N | OOS PnL total | worst DD | avg degradation |
+----------------------------------------------------------------------+
|                                                                      |
|  W1  [IS========][OOS==]                                             |
|  W2        [IS========][OOS==]                                       |
|  W3              [IS========][OOS==]                                 |
|  ...                                                                 |
|  2015-06   2015-12   2016-06   2016-12   ...                         |
+----------------------------------------------------------------------+
| (optional) stitched OOS equity curve on the same date axis           |
+----------------------------------------------------------------------+
| Status bar                                                           |
+----------------------------------------------------------------------+
```

- Replace the DataGrid with `<local:WalkForwardTimelineView DataContext="{Binding}" />` (or pass the rows collection).
- Make the chart height scale with window count: about 36 px per window, minimum 240 px, inside a vertical `ScrollViewer` for large runs.
- Keep the Walk-Forward settings expander above it unchanged.

## 7. Optional upgrades (after the basic chart works)

1. **Hover tooltip:** on pointer move, find the row under the cursor and show PnL, DD, trades, objective IS to OOS, degradation (same approach as the crosshair handler in `ChartView`).
2. **Click a bar:** select the window and show its details in a small panel under the chart (replaces the old table without needing 19 columns).
3. **Stitched OOS equity curve:** concatenate the OOS equity of each window, offset each by the previous window's ending balance, and draw it in a second plot that shares the x-axis range. This is the single most useful extra view for judging robustness.
4. **Degradation strip:** a thin bar chart under the staircase, one bar per window, aligned with each OOS segment.
5. **Export:** button that saves the chart as PNG (`Plot.SavePng(...)`) and the details as CSV, so the raw table is not lost.
6. **Table fallback:** keep a collapsed "Details" expander containing a slimmed DataGrid (Window, OOS PnL, DD, Trades, Degradation only) for anyone who wants raw numbers.

## 8. Step-by-step order

1. Snapshot with git (Section 3).
2. Add `WalkForwardTimelineBuilder` and a unit test: given 3 rows, it returns 3 IS and 3 OOS rectangles with the expected x-ranges and colour classes.
3. Add `WalkForwardTimelineView` with the `AvaPlot`, the theme, and a static render of the loaded rows (no interactions).
4. Put the new view into `RunnerView.axaml` next to the old grid and compare visually.
5. Hook it to Refresh: call `Render(...)` after `WalkForwardWindowRows` is filled, on the UI thread.
6. Verify Rolling and Anchored plans both look right (run each once).
7. Remove the DataGrid, the card view and the card-only ViewModel code (Section 3 "Delete").
8. Add colour thresholds, legend, summary line.
9. Add hover and click, then the optional extras you want.
10. Clean up unused resources and dead code. Build with 0 warnings.

## 9. Risks and checks

- **Dates vs bars:** if windows are stored as bar indices only, the date axis needs a lookup from index to timestamp. Decide this first (Section 4).
- **Thread safety:** update the plot only on the UI thread (`Dispatcher.UIThread`).
- **Many windows:** with 100+ windows, row labels crowd. Show every Nth tick label and rely on hover for detail.
- **Empty state:** when no batch is loaded, show a text overlay ("No walk-forward results. Run Walk-Forward, then Refresh.") instead of an empty chart.
- **Theme:** call the existing `ApplyPlotTheme()` so the chart matches the dark UI (the screenshot shows a black background).
- **Version differences:** ScottPlot method names vary by version. Check the version in the csproj before copying the snippets.

## 10. Definition of done

- Run a walk-forward and click Refresh: the staircase of IS and OOS bars appears on a real date axis, one row per window, labelled W1..Wn.
- OOS bars are coloured by result and show the OOS PnL.
- Both Rolling and Anchored runs render correctly.
- The old DataGrid and card view are gone, with no unused code or resources left.
- Switching batches redraws the chart. Clear empties it and shows the empty-state message.
- Build passes with 0 errors and 0 warnings.