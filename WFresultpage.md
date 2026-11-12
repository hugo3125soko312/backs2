# WalkForward UI State

## Current UI Layout

The WalkForward tab is split into two main sections:

### 1. WalkForward Settings (Top)
- **Window Mode**: "Anchored" (other option: "Rolling")
- **Length Unit**: "Percent" (other option: "Bars")
- **IS Length**: 60 (input box)
- **OOS Length**: 10 (input box)
- **Step Length**: 10 (input box)
- **Min Windows**: 3 (input box)
- **Objective**: EdgePercent (dropdown)
- **Carry Top N**: 1 (input box)
- **Plan Preview**: Shows the walk-forward plan based on current settings
- **Run Button**: Starts the walk-forward optimization
- **Cancel Button**: Stops running optimization

### 2. Results Display (Bottom)
After a run completes, results appear in:

#### DataGrid View (Default)
- Shows all walk-forward window results in a sortable DataGrid
- Columns: Batch, Window, Strategy, Symbol, Timeframe, Duration, Net PnL, CAGR, Edge %, Funded %, Ruin %, MC, Terminal, Drawdown, Best Day, Worst Day, Objective, Degradation
- Sortable by: WindowIndex, OosNetPnl, OosMaxDrawdown, OosTerminalBalance, OosTradeCount, IsObjective, OosObjective, Degradation
- Pagination: Top N results (default 200, adjustable)
- Batch selector: Choose which batch results to display

#### Card View (Toggle)
- Each walk-forward window is shown as a card with:
  - Color-coded verdict (Green/Red based on thresholds)
  - Window index, Net PnL, Max Drawdown, Terminal Balance
  - WfGood (>=0.70), WfBad (<=0.50), WfWarn (>0.50 and <0.70)
  - Robustness threshold: 0.70

#### Status Bar
- Shows walk-forward results count
- Sort key indicator
- Load status

---

# Chart Views

## Monte Carlo Tab Chart

The Monte Carlo tab has **three plot modes** that can be toggled:

### Plot Modes
1. **Histogram** (default): Shows distribution of returns
2. **Equity Curves**: Shows individual equity curve paths
3. **Cluster**: Shows clustering analysis visualization

### How Charts Work

**ScottPlot.Avalonia Integration**:
- Uses ScottPlot.Avalonia library for chart rendering
- Three AvaPlot controls: HistogramPlot, CurvesPlot, ClusterPlot
- All plot controls have pointer input disabled (keyboard/mouse navigation handled by parent)
- Plot theme applied via `ApplyPlotTheme()` method

**Render Pipeline**:
1. User clicks "Run Monte Carlo" → triggers `RunCommand`
2. Results stored in `MonteCarloResult` object
3. `EnsureVisiblePlotRendered()` checks if current plot matches result + mode
4. If cache miss, calls `RenderPlots()` → `RenderMaxDdHistogram()` or `RenderEquityCurves()`
5. Downsampling applied if curve has >1000 points (max 1000 points per curve)

**Downsampling Logic**:
- Axes downsampled to max 1000 points
- Values downsampled proportionally
- Selected curve indices limited to max 200 curves
- Downsample methods in `MonteCarloViewModel.Plots.cs`

**Equity Curves Rendering**:
- Each equity curve rendered with semi-transparent line
- Percentile bands shown as filled areas
- Success/Failure thresholds marked
- Uses `SelectCurveIndices()` to pick representative curves

### Plot Controls in XAML
```xml
<ScottPlot:AvaPlot x:Name="HistogramPlot" IsVisible="{Binding IsHistogramVisible}" />
<ScottPlot:AvaPlot x:Name="CurvesPlot" IsVisible="{Binding IsCurvesVisible}" />
<ScottPlot:AvaPlot x:Name="ClusterPlot" IsVisible="{Binding IsClusterVisible}" />
```

### Navigation
- Toggle between plots via `PlotModeOption` dropdown
- Checkbox to toggle equity curves visibility
- Arrow key handling for panning (tunnel-phase routed)

## Chart Tab

The Chart tab provides an interactive equity curve visualization:

### Features
- **Interactive Plot**: User can pan and zoom
- **Crosshair**: Shows exact point values on hover
- **Pointer Events**: Track mouse movement, update crosshair position
- **Pan/Zoom**: Arrow keys to pan, zoom controls

### How It Works
1. DataContext set to `ChartViewModel`
2. Plot initialized with `AvaPlot` control
3. Pointer movement tracked via `OnPlotPointerMoved()`
4. Crosshair cleared on pointer exit
5. Theme applied for dark mode consistency

### Code Behind
- `ChartView.axaml.cs` handles keyboard input
- Pan fraction: 10% per arrow key
- Zoom fraction: 10% per +/-
- Crosshair cleared on exit

### Navigation
- Shows selected strategy's equity curve
- Updates when strategy selection changes
- Displays live data from ChartViewModel

---

# Key Differences

| Feature | Monte Carlo Tab | Chart Tab |
|---------|----------------|-----------|
| Data Source | Computed results | Pre-saved equity curves |
| Plot Modes | Histogram, Equity Curves, Cluster | Single equity curve |
| Interaction | Run/Cancel controls | Interactive pan/zoom |
| Update Trigger | Manual (Run button) | Automatic (DataContext change) |
| Rendering | Complex (downsampling, percentiles) | Simple (single curve) |
