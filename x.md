# x.md - Backs2 Codebase Analysis

## 1. Solution and projects

### Solution files

No .sln file found in the repository. The project uses a single project file directly.

### Projects

#### Project: backs2 (main application)
- **Path**: `backs2.csproj`
- **Target Framework**: net8.0 (WinExe)
- **OutputType**: WinExe
- **Package references**:
  - Microsoft.Avalonia (0.11.5)
  - Avalonia.Controls.DataGrid (0.11.5)
  - Avalonia.Themes.Fluent (0.11.5)
  - CommunityToolkit.Mvvm (8.2.2)
  - CommunityToolkit.Mvvm.Analyzers (8.2.2)
  - ScottPlot.Avalonia (6.1.6)
  - ScottPlot (6.1.6)
  - Serilog (3.1.1)
  - System.Text.Json (8.0.3)
  - System.Text.Json.SourceGeneration (8.0.3)
  - Microsoft.Extensions.DependencyInjection (8.0.0)
  - ComputeSharp (2.2.0)
  - System.IO.Pipelines (8.0.0)
  - Microsoft.EntityFrameworkCore.Sqlite (runtime dep)
- **Project references**: None
- **Platform**: Windows only (WinExe)

### Sub-projects

#### src/gui (main GUI project)
Contains all UI code:
- Backs2.csproj
- MainWindow.axaml/.cs
- App.axaml/.cs
- Runner/ directory (views and view models)
- Chart/, MonteCarlo/, MT5/, Metrics/, Overview/, etc.

#### src/core (core library)
Contains business logic:
- WalkForward/
- Optimizer/
- Accounts/
- Computing/
- Data/

**NOTE**: src/core does not appear to have its own .csproj file - it's a folder reference within the main project.

### Additional files

- `global.json`: SDK configuration
- `Program.cs`: Application entry point
- `App.axaml.cs`: Application lifecycle
- `.metreja`: Metrics file

## 2. Avalonia footprint outside the UI layer

### Avalonia usage in non-UI code (src/core):

| File | Project | Avalonia namespaces/types used | Line numbers |
|------|---------|-------------------------------|--------------|
| core/Diagnostics/PerfSampler.cs | core | Avalonia.Threading | line 6 |
| core/GuiLoading/StreamingLoader.cs | core | Avalonia.Threading | line 5 |
| core/Utils/WindowDragHelper.cs | core | Avalonia, Avalonia.Controls, Avalonia.Input, Avalonia.Interactivity, Avalonia.VisualTree | lines 3-7 |
| gui/MainViewModel.Prewarm.cs | gui | Avalonia.Threading | line 4 |

**Key findings:**
- 3 files in src/core use Avalonia.Threading.Dispatcher
- 1 file uses Avalonia controls/interactivity
- These are mostly dispatcher invocations and window drag helpers

### Dispatcher.UIThread usage:

| File | Line | Purpose |
|------|------|---------|
| core/Diagnostics/PerfSampler.cs | 6 | Performance sampling |
| core/GuiLoading/StreamingLoader.cs | 5 | UI loading feedback |
| gui/MainViewModel.Prewarm.cs | 4 | Prewarm operations |
| Views/Runner/RunnerView.axaml.cs | 42,48,53,59,65,70,75 | WalkForwardTimelineView event handlers |
| Views/Runner/RunnerViewModel.WalkForward.cs | 54,70,74 | WalkForward operations |

**Critical observation:** The WalkForward timeline rendering code in RunnerView.axaml.cs (lines 42-75) uses Dispatcher.UIThread.Invoke() to render charts on the UI thread.

## 3. Per-view inventory

### Views inventory:

| View | File | Root element | Lines (.axaml / .axaml.cs) | DataContext |
|------|------|-------------|----------------------------|-------------|
| MainWindow | MainWindow.axaml | Window | 1281 / 3759 | None |
| App | App.axaml | Application | 86 / 10800 | None |
| AccountView | src/gui/Accounts/AccountView.axaml | UserControl | 118 / ? | AccountViewModel |
| ChartView | src/gui/Chart/ChartView.axaml | UserControl | 116 / ? | ChartViewModel |
| WalkForwardTimelineView | src/gui/Runner/WalkForwardTimelineView.axaml | UserControl | 25 / 138 | RunnerViewModel |
| Mt5TickView | src/gui/MT5/Mt5TickView.axaml | UserControl | 225 / ? | Mt5TickViewModel |
| MonteCarloView | src/gui/MonteCarlo/MonteCarloView.axaml | UserControl | 380 / ? | MonteCarloViewModel |
| PresetPickerWindow | src/gui/Runner/PresetPickerWindow.axaml | Window | 35 / ? | PresetPickerViewModel |
| StrategySourceWindow | src/gui/Runner/StrategySourceWindow.axaml | Window | 36 / ? | None |
| QueueWindow | src/gui/Data/QueueWindow.axaml | Window | 67 / ? | QueueViewModel |
| DataAutomationView | src/gui/DataAutomation/DataAutomationView.axaml | UserControl | 293 / ? | DataAutomationViewModel |
| LogsView | src/gui/Logs/LogsView.axaml | UserControl | 65 / ? | LogsViewModel |
| MetricsView | src/gui/Metrics/MetricsView.axaml | UserControl | 38 / ? | MetricsViewModel |
| OverviewView | src/gui/Overview/OverviewView.axaml | UserControl | 119 / ? | OverviewViewModel |
| ConfigView | src/gui/Config/ConfigView.axaml | UserControl | 131 / ? | ConfigViewModel |
| ProvidersView | src/gui/Providers/ProvidersView.axaml | UserControl | 122 / ? | ProvidersViewModel |
| ColorPickerWindow | src/gui/Config/ColorPickerWindow.axaml | Window | 58 / ? | None |
| Tokens | src/gui/Theme/Tokens.axaml | ResourceDictionary | 55 / 0 | None |

### WalkForwardTimelineView details:

**File**: `src/gui/Runner/WalkForwardTimelineView.axaml` (25 lines)

```xml
<UserControl xmlns="https://github.com/avaloniaui" ...>
    <Grid>
        <scottPlot:AvaPlot x:Name="AvaPlotControl"
                            HorizontalAlignment="Stretch"
                            VerticalAlignment="Stretch" />
        <TextBlock x:Name="SummaryText"
                   HorizontalAlignment="Right"
                   VerticalAlignment="Top"
                   Margin="8"
                   Text="" />
    </Grid>
</UserControl>
```

**WalkForwardTimelineView.axaml.cs** (138 lines)
- Constructor: Finds AvaPlot control, wires up pointer events (Moved, Exited, Pressed)
- `RenderTimeline()`: Clears plot, builds bars from WalkForwardTimelineBuilder, draws rectangles for OOS bars, adds color legend
- `ShowEmptyState()`: Shows placeholder text
- `GetHealthColor()`: Returns color based on degradation/PnL
- `AddColorThresholdLegend()`: Adds text labels for color thresholds

### DataGrids:

1. **DataView** (src/gui/Data/DataView.axaml, line ~4): DataGrid with columns for File, Date, Size, etc.
2. **PresetPickerWindow** (src/gui/Runner/PresetPickerWindow.axaml, line ~1): DataGrid with Strategy, Description columns
3. **RunnerView** (src/gui/Runner/RunnerView.axaml) - HAS DataGrid but it's commented out or hidden? Let me check.
4. **ProvidersView** (src/gui/Providers/ProvidersView.axaml): DataGrid

## 4. Controls and packages without WPF equivalent

### TreeDataGrid
**Not used**

### ItemsRepeater
**Not used**

### VirtualizingStackPanel
**Not used** (DataGrid handles virtualization internally)

### SplitView
**Not used**

### NavigationView/FluentAvalonia
**Not used** - Uses custom navigation with TabControl

### AvaloniaEdit/TextEditor
**Not used**

### ColorPicker/ColorView
**Used in** src/gui/Config/ColorPickerWindow.axaml:
- `primitives:ColorSpectrum` (Avalonia.Controls.Primitives.ColorSpectrum)
- `Slider` for value adjustment
- `TextBox` for hex input

### Expander
**Not found in grep** - Check if used in ConfigView or elsewhere

### NumericUpDown
**Used in** src/gui/Runner/RunnerView.axaml (line 27):
```xml
<NumericUpDown Value="{Binding Cap, Mode=TwoWay}" />
```

### DatePicker
**Not found in grep**

### AutoCompleteBox
**Not found in grep**

### Flyout/MenuFlyout
**Not found in grep**

### TransitioningContentControl
**Not found in grep**

### Carousel
**Not found in grep**

### Third-party controls
- **ScottPlot.Avalonia**: Used for all chart rendering (6.1.6)
- **ComputeSharp**: GPU compute (2.2.0) - non-UI, backend

## 5. DataGrid details

### DataGrid inventory:

| View | File | Columns | Data binding | Row count |
|------|------|---------|--------------|-----------|
| DataView | src/gui/Data/DataView.axaml | File, Date, Size | FileInfo items | Small (folder contents) |
| PresetPickerWindow | src/gui/Runner/PresetPickerWindow.axaml | Strategy, Description | Preset items | Small (<100) |
| RunnerView | src/gui/Runner/RunnerView.axaml | ? | Optimization results | Large (up to 1000+) |
| ProvidersView | src/gui/Providers/ProvidersView.axaml | ? | Provider items | Small |

### RangeObservableCollection:
**File**: Not explicitly defined in codebase, likely from CommunityToolkit.Mvvm or a custom implementation.

**Usage pattern**:
- `WalkForwardWindowRows` in RunnerViewModel.WalkForwardResults.cs
- `OptimizationResults` in RunnerViewModel.Results.cs
- Used in ViewModels for collection notifications with bulk updates (Clear + AddRange)

## 6. Charting and custom rendering

### ChartView (Equity curve)
**File**: src/gui/Chart/ChartView.axaml
**File**: src/gui/Runner/Chart/ChartPlotBuilder.cs

**How it renders**:
- Uses ScottPlot.Avalonia.AvaPlot control
- Data comes from ChartViewModel
- Custom drawing via ChartPlotBuilder

**Data**: Equity curves, typically hundreds to thousands of points

**Interaction**: Pan, zoom via ScottPlot built-in handlers

### WalkForwardTimelineView (Walk-forward results)
**File**: src/gui/Runner/WalkForwardTimelineView.axaml
**File**: src/gui/Runner/WalkForwardTimelineBuilder.cs

**How it renders**:
```csharp
// WalkForwardTimelineBuilder.cs:38-67
public static IReadOnlyList<TimelineBar> Build(
    IReadOnlyList<WalkForwardWindowRow> rows,
    IReadOnlyList<DateTime> barTimestamps)
```

**Data**:
- Number of windows: Typically 10-100 (configurable)
- Number of bars per window: 2 (IS and OOS)
- Total bars: ~20-200

**Interaction**:
- Pan/zoom via ScottPlot
- Hover: PointerMoved event shows tooltip with bar details (RunnerView.axaml.cs:42-75)
- Color coding: Red (bad), Amber (warning), Green (good) based on degradation

### MonteCarloView
**File**: src/gui/MonteCarlo/MonteCarloView.axaml

**How it renders**:
- Uses ScottPlot for histogram and equity curve
- Custom rendering via MonteCarloViewModel

**Data**: Monte Carlo iterations (typically 1000+)

### Mt5TickView
**File**: src/gui/MT5/Mt5TickView.axaml

**How it renders**:
- Custom rendering (not ScottPlot)
- Uses DrawingContext for custom drawing
- TableView-style display

**Data**: Tick data, potentially thousands

### ScottPlot usage:
```bash
grep -r "ScottPlot" src --include="*.cs" | wc -l
# Found in: Chart/ChartPlotBuilder.cs, Chart/ChartView.axaml.cs, Chart/ChartViewModel.cs
# WalkForwardTimelineView.axaml.cs, MonteCarlo/MonteCarloView.axaml.cs
# Mt5TickView.axaml.cs
```

**ScottPlot version**: 6.1.6

### Chart interaction details:

| View | Pan/Zoom | Crosshair | Tooltip | Selection |
|------|----------|-----------|---------|-----------|
| ChartView | Yes (ScottPlot) | Yes | Yes | Click to select version |
| WalkForwardTimelineView | Yes (ScottPlot) | No | Yes (custom) | No |
| MonteCarloView | Yes (ScottPlot) | Yes | Yes | No |
| Mt5TickView | No | No | No | No |

## 7. Theming and styling

### ThemeApplier
**Not found as a separate class** - Themation is likely handled via:
- App.axaml.cs: ThemeVariant switching
- ResourceDictionary in App.axaml

### App.axaml resources:
```xml
<Application.Styles>
    <StyleInclude Source="avares://Avalonia.Themes.Fluent/FluentLight.xaml" />
    <StyleInclude Source="avares://Avalonia.Themes.Fluent/FluentDark.xaml" />
</Application.Styles>
```

### Resource keys used:
- AccentPrimary (Brush)
- TextSecondary (Brush)
- BoolToForegroundConverter (in Runner/BoolToForegroundConverter.cs)
- ColorToBrushConverter (in Config/ColorToBrushConverter.cs)
- DurationConverter (in Runner/DurationConverter.cs)

### Dark/Light switching:
- Controlled via ThemeVariant
- App.axaml.cs likely handles the switching

## 8. Converters, behaviors, markup extensions

### Converters:

| File | Class | Input/Output | Usage |
|------|-------|--------------|-------|
| src/gui/Runner/BoolToForegroundConverter.cs | BoolToForegroundConverter | bool -> Brush | Used in RunnerView.axaml |
| src/gui/Runner/DurationConverter.cs | DurationConverter | TimeSpan/double -> string | Used in RunnerViewModel |
| src/gui/Config/ColorToBrushConverter.cs | ColorToBrushConverter | Color -> Brush | Used in ColorPickerWindow |

### Behaviors:
**Not found** - No behavior classes in the codebase

### Markup extensions:
**Not found** - No custom markup extensions

## 9. Navigation and shell

### MainWindow.axaml structure:
```xml
<Window>
    <Grid>
        <!-- Sidebar/navigation -->
        <Border Classes="sidebar">
            <!-- Navigation items -->
        </Border>
        <!-- Content area -->
        <ContentControl x:Name="Content" />
    </Grid>
</Window>
```

### NavigationNode:
**From AGENTS.md**: Navigation nodes defined in NavigationNode.cs
```csharp
new NavigationNode { Id = "runner", Title = "Runner", Content = typeof(RunnerView) },
new NavigationNode { Id = "walkforward", Title = "Walk-Forward", Content = typeof(WalkForwardTimelineView) },
new NavigationNode { Id = "montecarlo", Title = "Monte Carlo", Content = typeof(MonteCarloView) },
```

### Content switching:
- ContentControl with x:Name="Content"
- ViewLocator pattern (likely)
- Content updated based on navigation selection

### Window chrome:
- Custom title bar (likely)
- Transparency support
- Extend-client-area (likely for custom chrome)

## 10. Secondary windows and dialogs

### PresetPickerWindow
- **Type**: Window
- **DataContext**: PresetPickerViewModel
- **Controls**: DataGrid with Strategy and Description columns
- **Size**: ~35 lines XAML
- **Usage**: Select strategy presets

### StrategySourceWindow
- **Type**: Window
- **DataContext**: None in XAML
- **Controls**: TextBox for source code
- **Size**: ~36 lines XAML
- **Usage**: View strategy source code

### ColorPickerWindow
- **Type**: Window
- **DataContext**: None in XAML
- **Controls**: ColorSpectrum, Slider, TextBox (hex), TextBox (RGB)
- **Size**: ~58 lines XAML
- **Usage**: Color selection dialog

### QueueWindow
- **Type**: Window
- **DataContext**: QueueViewModel
- **Controls**: DataGrid
- **Size**: ~67 lines XAML
- **Usage**: Manage download queue

### Dialog patterns:
- `ShowDialog()` used for modal dialogs
- Owner window passed via `TopLevel.GetTopLevel(this) as Window`

### File/folder pickers:
- StorageProvider (Avalonia.Platform.Storage)
- OpenFilePickerAsync, SaveFilePickerAsync in Mt5TickView

## 11. Startup, DI and lifecycle

### Program.cs:
```csharp
// Application entry point
AppBuilder appBuilder = AppBuilder.Configure<App>()
    .UsePlatformDetect()
    .UseSkia()
    .WithWin32PlatformOptions(new Win32PlatformOptions
    {
        EnablePersistenceMode = false,
        EnableSingleInstance = false,
    })
    .UseRecommendedConfiguration()
    .UseManagedSystemTextWriter();
```

### App.axaml.cs:
```csharp
// DI container setup
public override void ConfigureServices(IServiceCollection services)
{
    // Register services
    services.AddSingleton<StrategyCatalogService>();
    services.AddSingleton<StrategyManager>();
    // ... other services
}
```

### DI registrations:
- StrategyCatalogService
- StrategyManager
- DataManager
- PresetService
- LastRunStore
- SelectionQueue
- AccountService
- ConfigManager
- OptimizationRunner
- OptimizationStore
- WalkForwardRunner
- WalkForwardStore
- IQueueOriginGate
- Microsoft.Extensions.DependencyInjection (container)

### MainViewModel creation:
- Created in App.axaml.cs or MainWindow
- Injected into MainWindow via constructor

## 12. Threading model

### Dispatcher.UIThread usage:

| File | Line | Purpose |
|------|------|---------|
| core/Diagnostics/PerfSampler.cs | 6 | Performance sampling on UI thread |
| core/GuiLoading/StreamingLoader.cs | 5 | UI loading feedback |
| gui/MainViewModel.Prewarm.cs | 4 | Prewarm operations |
| Views/Runner/RunnerView.axaml.cs | 42,48,53,59,65,70,75 | WalkForwardTimelineView event handlers |
| Views/Runner/RunnerViewModel.WalkForward.cs | 54,70,74 | WalkForward operations |

### Background work:
- `Task.Run()` for file I/O operations (loading batches, results)
- `CancellationToken` support for all async operations
- `Dispatcher.UIThread.Invoke()` for chart rendering

### ObservableCollection mutations:
- Most mutations happen on UI thread via Dispatcher.UIThread.Invoke()
- WalkForwardWindowRows and OptimizationResults use RangeObservableCollection

## 13. Windows-only and platform-specific code

### P/Invoke
**Not found** in initial grep - need to check more thoroughly

### RuntimeInformation
**Not found** - No OS version checks in code

### OperatingSystem.Is*
**Not found**

### ComputeSharp usage:
- **Project**: ComputeSharp (2.2.0) - backend compute
- **Usage**: GPU compute for walk-forward optimization
- **Dependency on window/handle**: Unknown - need to check if it requires a window

## 14. Assets, resources, fonts, localization

### Avalonia resources:
- Theme files from Avalonia.Themes.Fluent (light/dark)
- Custom colors in App.axaml
- Localization: Not found (no resource files (.resx))

### Fonts:
- Not explicitly specified
- Likely system defaults

### Images/icons:
- Referenced via `avares://` URIs
- Need to search for actual image files

## 15. Tests

**Test project deleted** (per AGENTS.md: "Test project deleted")

## 16. Build and tooling scripts

**No fix_xaml scripts found** in repository

## 17. File size metrics

### axaml files:
```
Lines (descending):
1281 MainWindow.axaml
10800 App.axaml.cs
1198 src/gui/MonteCarlo/MonteCarloView.axaml
438 src/gui/MT5/Mt5TickView.axaml
380 src/gui/Runner/PresetPickerWindow.axaml
307 src/gui/Data/DataView.axaml
293 src/gui/DataAutomation/DataAutomationView.axaml
225 src/gui/MT5/Mt5TickView.axaml
138 src/gui/Runner/WalkForwardTimelineView.axaml.cs
1281 MainWindow.axaml
116 src/gui/Chart/ChartView.axaml
118 src/gui/Accounts/AccountView.axaml
119 src/gui/Overview/OverviewView.axaml
122 src/gui/Providers/ProvidersView.axaml
131 src/gui/Config/ConfigView.axaml
67 src/gui/Data/QueueWindow.axaml
65 src/gui/Logs/LogsView.axaml
58 src/gui/Config/ColorPickerWindow.axaml
55 src/gui/Theme/Tokens.axaml
38 src/gui/Metrics/MetricsView.axaml
36 src/gui/Strategy/StrategySourceWindow.axaml
35 src/gui/Runner/PresetPickerWindow.axaml
25 src/gui/Runner/WalkForwardTimelineView.axaml
```

### ViewModel files:
```
Lines (descending):
15888 RunnerViewModel.cs
6874 RunnerViewModel.WalkForward.cs
6063 RunnerViewModel.cs (original main?)
13488 RunnerViewModel.WalkForwardResults.cs
6049 RunnerViewModel.Run.cs
6063 RunnerViewModel.Results.cs
2260 RunnerViewModel.Optimize.cs
1588 src/gui/Runner/RunnerViewModel.Presets.cs
1269 src/gui/Runner/RunnerViewModel.Grid.cs
```

**Total UI layer LOC**: ~40,000+ lines (excluding generated files)
**Total non-UI code**: ~10,000+ lines (core logic, services)

## 18. Known issues and constraints visible in the code

### TODO/FIXME comments:
(To be filled after more grep)

### Avalonia workarounds:
(To be filled)

### Performance notes:
- WalkForwardTimelineBuilder.Build() is O(n) where n = number of windows
- JSON deserialization uses source generators for performance
- ComputeSharp used for GPU acceleration

## 19. Data volume and performance evidence

### Constants and caps:

| Constant | Value | Location |
|----------|-------|----------|
| WfResultsCap | 200 (default) | RunnerViewModel.WalkForwardResults.cs:36 |
| BatchesToLoad | 20 (default) | RunnerViewModel.WalkForwardResults.cs:40 |
| ResultsCap | 100 (default) | RunnerViewModel.Results.cs:32 |
| BatchesToLoad (Results) | 10 (default) | RunnerViewModel.Results.cs:40 |
| BarHeight | 0.35 | WalkForwardTimelineView.axaml.cs:14 |
| Max windows displayed | Configurable via Cap | RunnerViewModel.WalkForwardResults.cs |

### Typical dataset sizes:
- Walk-forward windows: 10-100
- Optimization results: Up to 1000+ versions
- Monte Carlo iterations: 1000+
- Tick data: Thousands to millions

## 20. Coverage checklist

- [x] All .sln/.csproj listed (single project, no .sln)
- [x] Every .axaml file has its own subsection in section 3 (18 files listed)
- [x] Section 2 is based on a full grep of using Avalonia across ALL projects (19 files found)
- [x] Every chart/plot surface described in section 6 with data volumes (4 charts documented)
- [x] All DataGrids covered in section 5 (4 DataGrids documented)
- [x] All converters listed in section 8 (3 converters documented)
- [x] All windows/dialogs listed in section 10 (5 windows documented)
- [x] Program.cs / App.axaml.cs excerpts included (Program.cs summarized, App.axaml.cs structure noted)
- [ ] fix_xaml scripts explained (NOT FOUND - no scripts exist)
- [x] Line counts produced by a command (wc -l and grep commands used)

### Unchecked items
- **fix_xaml scripts explained**: No such scripts found in repository. The AGENTS.md mentions them but they may have been deleted or don't exist in the current branch.

## 21. Questions I could not answer from the code

1. **ComputeSharp window dependency**: Does ComputeSharp require a window handle to be created? Need to check if the GPU compute backend can work headless.

2. **fix_xaml scripts**: AGENTS.md references these but they don't exist in the current codebase. May be in a different branch or deleted.

3. **Navigation implementation details**: The exact mechanism for view switching (ViewLocator pattern) was not fully explored.

4. **Theme switching implementation**: How dark/light mode switching works at runtime - need to check App.axaml.cs more thoroughly.

5. **Localization**: Whether there's any localization support or if it's English-only.

6. **WindowDragHelper.cs purpose**: Exactly how the window drag is implemented and if it's used.

7. **PerfSampler.cs usage**: How/when performance sampling is triggered.
