### Common Issues

| Error | Cause | Fix |
|-------|-------|-----|
| `CS0117: SourceGenerationContext not found` | Missing `System.Text.Json.SourceGeneration` package | Add package reference |
| `CS8632: nullable reference types` | Missing nullable context | Enable `<Nullable>enable</Nullable>` |

---

## Project Structure

### Current Architecture

```
backs2/
├── backs2.csproj              # Main project (WinExe, net8.0)
├── AGENTS.md                  # AI agent documentation
├── project_oop.md             # Architecture docs
├── docs/                      # Additional documentation
│   ├── audit-report.md
│   ├── PERF_AUDIT_LLM_BUGS.md
│   └── ...
├── src/
│   ├── gui/                   # WPF UI
│   │   ├── MainWindow.xaml
│   │   ├── App.xaml
│   │   ├── RunnerView.xaml
│   │   ├── ConfigView.xaml
│   │   ├── Overview/
│   │   │   └── OverviewView.xaml
│   │   ├── Metrics/
│   │   │   └── MetricsView.xaml
│   │   ├── Accounts/
│   │   │   └── AccountView.xaml
│   │   ├── Data/
│   │   │   ├── DataView.xaml
│   │   │   └── QueueWindow.xaml
│   │   ├── Providers/
│   │   │   └── ProvidersView.xaml
│   │   ├── Logs/
│   │   │   └── LogsView.xaml
│   │   ├── MonteCarlo/
│   │   │   └── MonteCarloView.xaml
│   │   ├── Chart/
│   │   │   └── ChartView.xaml
│   │   ├── DataAutomation/
│   │   │   └── DataAutomationView.xaml
│   │   ├── MT5/
│   │   │   └── Mt5TickView.xaml
│   │   ├── Runner/
│   │   │   ├── WalkForwardTimelineView.xaml
│   │   │   ├── PresetPickerWindow.xaml
│   │   │   ├── BoolToForegroundConverter.cs
│   │   │   ├── DurationConverter.cs
│   │   │   └── BoolToVisibilityConverter.cs
│   │   ├── Strategy/
│   │   │   └── StrategySourceWindow.xaml
│   │   └── Services/
│   │       ├── IDialogService.cs
│   │       └── WpfDialogService.cs
│   ├── core/                  # Core logic
│   │   ├── WalkForward/
│   │   │   └── WalkForwardJsonContext.cs  # JSON source generation
│   │   ├── Optimizer/
│   │   ├── Accounts/
│   │   ├── Compute/
│   │   ├── Diagnostics/
│   │   ├── GuiLoading/
│   │   ├── Data/
│   │   ├── DataAutomation/
│   │   ├── MT5/
│   │   ├── Metrics/
│   │   ├── Overview/
│   │   ├── Providers/
│   │   └── ...
│   └── compute/               # Compute backends
└── data/
    ├── cache/
    └── walkforward/
```