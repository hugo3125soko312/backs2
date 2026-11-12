# Backs2 Project - Comprehensive Architecture Documentation

**Project:** backs2  
**Type:** Avalonia UI Desktop Application (.NET 8)  
**Purpose:** Walk-forward optimizer for trading strategies with Monte Carlo simulation, GPU computing, and multi-provider data support.

---

## Table of Contents
1. [Core Architecture](#core-architecture)
2. [GUI Layer](#gui-layer)
3. [Core Services](#core-services)
4. [Computing & Performance](#computing--performance)
5. [Data Layer](#data-layer)
6. [Walk-Forward Optimization](#walk-forward-optimization)
7. [Monte Carlo Simulation](#monte-carlo-simulation)
8. [Account & Configuration](#account--configuration)
9. [Diagnostics & Monitoring](#diagnostics--monitoring)
10. [Scripts & Tooling](#scripts--tooling)
11. [File Structure](#file-structure)
12. [Performance Audit Summary](#performance-audit-summary)

---

## Core Architecture

### Main Entry Point
- **Program.cs** - Application startup, DI container setup, theme initialization

### Application Structure
```
App.axaml - Main Application class
MainWindow.axaml - Main window with navigation
NavigationNode.cs - Navigation structure for sidebar
```

---

## GUI Layer

### Views (18 .axaml files)
| File | Purpose |
|------|---------|
| MainWindow.axaml | Main application window with sidebar navigation |
| RunnerView.axaml | Strategy runner with walk-forward, optimize, run controls |
| WalkForwardTimelineView.axaml | Timeline chart showing IS/OOS performance bars |
| ChartView.axaml | Interactive equity curve with pan/zoom/crosshair |
| DataView.axaml | Data management and queue view |
| MonteCarloView.axaml | Monte Carlo simulation results with plots |
| ConfigView.axaml | Application configuration and themes |
| ProvidersView.axaml | Provider/dataset management |
| DataAutomationView.axaml | Data automation workflows |
| LogsView.axaml | Application logs |
| Mt5TickView.axaml | MT5 tick data visualization |
| MetricsView.axaml | Performance metrics display |
| OverviewView.axaml | Strategy overview dashboard |
| AccountView.axaml | Account/firm configuration |
| PresetPickerWindow.axaml | Parameter preset selection |
| StrategySourceWindow.axaml | Strategy source code viewer |
| ColorPickerWindow.axaml | Color picker utility |
| QueueWindow.axaml | Queue management window |

### ViewModels (Main)
| File | Class | Purpose |
|------|-------|---------|
| MainViewModel.cs | MainViewModel | Application shell, navigation, prewarm |
| ChartViewModel.cs | ChartViewModel | Chart state, crosshair, downsampling |
| DataViewModel.cs | DataViewModel | Data management state |
| MonteCarloViewModel.cs | MonteCarloViewModel | Monte Carlo simulation state |

### Runner Module (Core)
| File | Class | Purpose |
|------|-------|---------|
| RunnerViewModel.cs | RunnerViewModel | Main runner state, strategies, parameters |
| RunnerViewModel.Run.cs | RunnerViewModel.Run | Run execution logic |
| RunnerViewModel.Optimize.cs | RunnerViewModel.Optimize | Optimization pipeline |
| RunnerViewModel.WalkForward.cs | RunnerViewModel.WalkForward | Walk-forward setup |
| RunnerViewModel.WalkForwardResults.cs | RunnerViewModel.WalkForwardResults | Results display, data loading |
| RunnerViewModel.Presets.cs | RunnerViewModel.Presets | Preset management |
| RunnerViewModel.Grid.cs | RunnerViewModel.Grid | DataGrid customizations |
| RunnerParameterViewModel.cs | RunnerParameterViewModel | Individual parameter VM |

### Walk-Forward Timeline
| File | Class | Purpose |
|------|-------|---------|
| WalkForwardTimelineBuilder.cs | WalkForwardTimelineBuilder | Converts rows to timeline bars |
| WalkForwardTimelineView.axaml.cs | WalkForwardTimelineView | Canvas-based timeline rendering |
| WalkForwardTimelineView.axaml | WalkForwardTimelineView | XAML layout |
| WalkForwardWindowRow.cs | WalkForwardWindowRow | Single window result data |
| WalkForwardWindowCard.cs | WalkForwardWindowCard | Card view (legacy) |

### Themes & Styling
| File | Class | Purpose |
|------|-------|---------|
| ThemeApplier.cs | ThemeApplier | Dark/light theme application |
| ColorToBrushConverter.cs | ColorToBrushConverter | Color to brush conversion |

---

## Core Services

### Accounts
| File | Class | Purpose |
|------|-------|---------|
| AccountService.cs | AccountService | Account management, firm loading |
| AccountJsonContext.cs | AccountJsonContext | JSON source generation |
| AccountPhase.cs | AccountPhase | Account phase enum |
| FirmConfig.cs | FirmConfig | Firm configuration |

### Configuration
| File | Class | Purpose |
|------|-------|---------|
| ConfigManager.cs | ConfigManager | Settings persistence, JSON config |
| PresetService.cs | PresetService | Parameter preset CRUD |

### Data Management
| File | Class | Purpose |
|------|-------|---------|
| DataManager.cs | DataManager | Central data access |
| WalkForwardStore.cs | WalkForwardStore | Batch result storage |
| WalkForwardAggregator.cs | WalkForwardAggregator | Statistics and curve stitching |
| WalkForwardPlanner.cs | WalkForwardPlanner | Window planning |
| WalkForwardObjective.cs | WalkForwardObjective | Objective functions |
| WalkForwardRunner.cs | WalkForwardRunner | Runner execution |
| WalkForwardWindowPlanner.cs | WalkForwardWindowPlanner | Window planning logic |
| WalkForwardModels.cs | WalkForwardModels | Data models |
| WalkForwardJsonContext.cs | WalkForwardJsonContext | JSON source gen |

### Selection & Queue
| File | Class | Purpose |
|------|-------|---------|
| SelectionQueue.cs | SelectionQueue | Strategy/parameter queue |
| QueueGateResult.cs | QueueGateResult | Queue decision |
| IQueueOriginGate.cs | IQueueOriginGate | Queue gate interface |

### Optimization
| File | Class | Purpose |
|------|-------|---------|
| OptimizationRunner.cs | OptimizationRunner | Optimization pipeline |
| WalkForwardObjective.cs | WalkForwardObjective | Objective scoring |

### Diagnostics
| File | Class | Purpose |
|------|-------|---------|
| PerfSampler.cs | PerfSampler | Performance measurement |
| PerfSnapshot.cs | PerfSnapshot | Snapshot data |
| PerfThresholds.cs | PerfThresholds | Threshold definitions |
| NvmlProbe.cs | NvmlProbe | NVIDIA GPU monitoring |
| VramProbe.cs | VramProbe | VRAM monitoring |

---

## Computing & Performance

### Compute Backends
| File | Class | Purpose |
|------|-------|---------|
| IComputeBackend.cs | IComputeBackend | Compute backend interface |
| CpuBackend.cs | CpuBackend | CPU compute backend |
| GpuBackend.cs | GpuBackend | GPU compute backend |
| CpuComputeDispatcher.cs | CpuComputeDispatcher | CPU dispatch manager |
| ComputeWorkerManager.cs | ComputeWorkerManager | Worker management |
| SimulationKernelCpu.cs | SimulationKernelCpu | CPU simulation kernel |
| SimulationKernelGpu.cs | SimulationKernelGpu | GPU simulation kernel |
| JobConfig.cs | JobConfig | Job configuration |
| JobObjectHandle.cs | JobObjectHandle | Job handle |
| JobProgress.cs | JobProgress | Job progress |
| WorkerEnvelope.cs | WorkerEnvelope | Worker communication |
| WorkerHost.cs | WorkerHost | Worker process host |
| WorkerIpc.cs | WorkerIpc | IPC communication |

### Analysis
| File | Class | Purpose |
|------|-------|---------|
| KMeansClusterer.cs | KMeansClusterer | K-means clustering |
| BatchDeterminismCheck.cs | BatchDeterminismCheck | Result determinism verification |

---

## Data Layer

### MT5 Integration
| File | Class | Purpose |
|------|-------|---------|
| Mt5TickServer.cs | Mt5TickServer | MT5 tick server |
| Mt5TickService.cs | Mt5TickService | Tick service |
| Mt5TickParser.cs | Mt5TickParser | Tick parsing |
| Mt5Models.cs | Mt5Models | MT5 data models |
| Mt5Tick.cs | Mt5Tick | Tick structure |
| Mt5TickViewModel.cs | Mt5TickViewModel | Tick display VM |
| Mt5TickViewModel.Parsing.cs | Mt5TickViewModel.Parsing | Parsing logic |
| Mt5TickViewModel.Extraction.cs | Mt5TickViewModel.Extraction | Extraction logic |
| Mt5TickViewModel.Tkc.cs | Mt5TickViewModel.Tkc | TKC handling |

### Tick Compiler
| File | Class | Purpose |
|------|-------|---------|
| Mt5TickCompiler.cs | Mt5TickCompiler | MT5 tick compilation |

### Data Automation
| File | Class | Purpose |
|------|-------|---------|
| AnalysisService.cs | AnalysisService | Data analysis |
| ExtractionCore.cs | ExtractionCore | Data extraction |
| ParsingCore.cs | ParsingCore | Data parsing |
| BatchedOcrRecognizer.cs | BatchedOcrRecognizer | OCR recognition |
| OcrWorkerClient.cs | OcrWorkerClient | OCR worker |
| OcrWorkerHost.cs | OcrWorkerHost | OCR worker host |

### Streaming
| File | Class | Purpose |
|------|-------|---------|
| StreamingLoader.cs | StreamingLoader | Data streaming |
| IStreamingLoaderFactory.cs | IStreamingLoaderFactory | Factory interface |

---

## Walk-Forward Optimization

### Core Pipeline
| File | Class | Purpose |
|------|-------|---------|
| WalkForwardRunner.cs | WalkForwardRunner | Main runner |
| WalkForwardPlanner.cs | WalkForwardPlanner | Window planning |
| WalkForwardWindowPlanner.cs | WalkForwardWindowPlanner | Window details |
| WalkForwardObjective.cs | WalkForwardObjective | Objective scoring |
| WalkForwardAggregator.cs | WalkForwardAggregator | Results aggregation |
| WalkForwardStore.cs | WalkForwardStore | Storage |
| WalkForwardModels.cs | WalkForwardModels | Models |
| WalkForwardWindowRow.cs | WalkForwardWindowRow | Result row |
| WalkForwardBar.cs | WalkForwardBar | Timeline bar |
| WalkForwardTimelineBuilder.cs | WalkForwardTimelineBuilder | Bar builder |
| WalkForwardTimelineView.axaml.cs | WalkForwardTimelineView | Timeline view |

### Key Types
```csharp
WalkForwardSummary - Aggregated results
WalkForwardWindowResult - Single window result
WalkForwardBatch - Batch of windows
WalkForwardProgress - Progress reporting
```

---

## Monte Carlo Simulation

| File | Class | Purpose |
|------|-------|---------|
| MonteCarloViewModel.cs | MonteCarloViewModel | Simulation state |
| MonteCarloViewModel.Plots.cs | MonteCarloViewModel.Plots | Plot rendering |
| MonteCarloViewModel.Stats.cs | MonteCarloViewModel.Stats | Statistics |
| MonteCarloViewModel.Cluster.cs | MonteCarloViewModel.Cluster | Clustering |
| MonteCarloDispatchResult.cs | MonteCarloDispatchResult | Dispatch result |

**Key Features:**
- 3 plot modes: Histogram, Equity Curves, Cluster
- GPU acceleration via ComputeSharp
- GPU-accelerated equity curve computation
- Interactive pan/zoom/crosshair

---

## Account & Configuration

| File | Class | Purpose |
|------|-------|---------|
| AccountService.cs | AccountService | Account management |
| ConfigManager.cs | ConfigManager | Settings persistence |
| PresetService.cs | PresetService | Preset CRUD |
| FirmConfig.cs | FirmConfig | Firm configuration |

**Key Concepts:**
- AccountDescriptor - Account metadata
- ActiveFundedContext - Resolved context with funded phase
- PlanSize - Plan size categories
- AccountPhase - Phase in account lifecycle

---

## Diagnostics & Monitoring

| File | Class | Purpose |
|------|-------|---------|
| PerfSampler.cs | PerfSampler | Performance sampling |
| PerfSnapshot.cs | PerfSnapshot | Snapshot data |
| PerfThresholds.cs | PerfThresholds | Thresholds |
| LogManager.cs | LogManager | Logging |
| NvmlProbe.cs | NvmlProbe | NVIDIA monitoring |
| VramProbe.cs | VramProbe | VRAM monitoring |

**Performance Features:**
- GPU VRAM monitoring
- NVIDIA device probing
- Performance thresholds
- Detailed timing logs

---

## Scripts & Tooling

| File | Purpose |
|------|---------|
| fix_xaml.py | XAML formatting script |
| fix_xaml.ps1 | PowerShell XAML formatter |
| RunOcr.ps1 | OCR runner |
| Mt5HistoryDownloader.ps1 | MT5 history downloader |
| Mt5TkcDownload.ps1 | TKC download script |

---

## Performance Audit Summary

### Issues Found: 42
### Issues Fixed: 6

**Fixed Issues:**
1. ✅ ProvidersViewModel.Contains → HashSet (O(n) → O(1))
2. ✅ Materialized LINQ in Aggregator → Streaming
3. ✅ Brush caching in WalkForwardTimelineView
4. ✅ Null checks in WalkForwardTimelineView
5. ✅ WalkForwardTimelineBuilder refactoring
6. ✅ Comment cleanup in RunnerViewModel

**Remaining Recommendations:**
- Monitor dataset sizes for large walk-forward runs
- Consider streaming for very large datasets
- Add ConfigureAwait(false) in library code

### Key Performance Patterns
- **LINQ Usage:** Heavy use of LINQ with some materialization issues
- **Compute:** GPU-accelerated via ComputeSharp
- **I/O:** Synchronous file I/O in UI-triggered paths
- **Threading:** Lock contention in AccountService (read-only, acceptable)
- **Collections:** Generic collections throughout, no boxing issues

---

## Key Architectural Decisions

1. **Avalonia UI** - Cross-platform desktop framework
2. **ComputeSharp** - GPU compute via HLSL
3. **ScottPlot** - Charting (future integration)
4. **CommunityToolkit.Mvvm** - MVVM pattern
5. **JSON Source Generators** - Fast JSON serialization
6. **DI Container** - Microsoft.Extensions.DependencyInjection
7. **Multi-provider architecture** - Extensible data sources
8. **Walk-forward pattern** - IS/OOS testing pipeline

---

## Data Flow

```
Data Sources → MT5 Tick Compiler → CacheManager → DataManager
                                                    ↓
Optimization ← Runner ← Strategy Queue ← Preset Service
              ↓
         Walk-Forward ← WalkForwardStore → WalkForwardAggregator → Charts
              ↓
         Monte Carlo ← ComputeSharp GPU → Results → Metrics
```

---

## Extensibility Points

1. **Providers** - Add new data providers via IDataOriginResolver
2. **Compute Backends** - Extend with new ComputeSharp kernels
3. **Charting** - Add new plot types via ChartSeriesFactory
4. **Walk-Forward** - Custom objectives via WalkForwardObjective
5. **Themes** - Extend via ConfigViewModel.Theme

---

## Testing Notes

- Tests use xUnit
- RangeObservableCollection used for bulk notifications
- PerfSampler for performance measurements
- Determinism checks for reproducibility

---

## Next Steps

1. **Chart Integration** - Complete WalkForwardTimelineView with ScottPlot
2. **Performance Monitoring** - Add real-time monitoring to UI
3. **Parallelization** - Add more parallel processing for batches
4. **Caching** - Implement result caching for repeated runs
5. **Export** - Add data export to CSV/Excel
