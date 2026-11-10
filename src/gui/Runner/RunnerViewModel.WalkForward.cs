using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core;
using backs2.Core.Optimizer;
using backs2.Core.WalkForward;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    private CancellationTokenSource? _walkForwardCts;
    private int _datasetBars; // 0 = unknown; set once data is fetched for the current dataset

    [ObservableProperty] private string _wfWindowMode = "Anchored";
    [ObservableProperty] private string _wfLengthUnit = "Percent";
    [ObservableProperty] private double _wfIsLength = 60;
    [ObservableProperty] private double _wfOosLength = 10;
    [ObservableProperty] private double _wfStepLength = 10;
    [ObservableProperty] private int _wfMinWindows = 3;
    [ObservableProperty] private string _wfObjective = "EdgePercent";
    [ObservableProperty] private int _wfCarryTopN = 1;

    [ObservableProperty] private string _wfPlanPreviewText = "";
    [ObservableProperty] private bool _wfIsPlanValid = true;
    [ObservableProperty] private string _wfPlanInvalidReason = "";

    [ObservableProperty] private bool _isWalkForwardRunning;
    [ObservableProperty] private double _wfProgress;
    [ObservableProperty] private string _wfEtaText = "";

    public IReadOnlyList<string> WfWindowModeValues => AppSettings.WalkForwardWindowModeValues;
    public IReadOnlyList<string> WfLengthUnitValues => AppSettings.WalkForwardLengthUnitValues;
    public IReadOnlyList<string> WfObjectiveValues => WalkForwardObjective.SupportedKeys;

    /// <summary>Available column sort keys for the walk-forward results DataGrid.</summary>
    public IReadOnlyList<string> AvailableWfSortKeys { get; } = new[]
    { "WindowIndex", "OosNetPnl", "OosMaxDrawdown", "OosTerminalBalance", "OosTradeCount", "IsObjective", "OosObjective", "Degradation" };

    /// <summary>Run enabled when a strategy is loaded, the queue has one ticker, we are not busy, and the plan validates.</summary>
    public bool CanWalkForward =>
        SelectedStrategy != null && !IsBusy && WfIsPlanValid && QueuedItems.Count > 0;

    partial void OnWfWindowModeChanged(string value)
    {
        _config.Settings.WalkForwardWindowMode = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfLengthUnitChanged(string value)
    {
        _config.Settings.WalkForwardLengthUnit = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfIsLengthChanged(double value)
    {
        _config.Settings.WalkForwardIsLength = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfOosLengthChanged(double value)
    {
        _config.Settings.WalkForwardOosLength = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfStepLengthChanged(double value)
    {
        _config.Settings.WalkForwardStepLength = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfMinWindowsChanged(int value)
    {
        _config.Settings.WalkForwardMinWindows = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfObjectiveChanged(string value)
    {
        _config.Settings.WalkForwardObjective = value;
        _config.SaveConfig();
        RefreshWfPlanPreview();
    }

    partial void OnWfIsPlanValidChanged(bool value) => OnPropertyChanged(nameof(CanWalkForward));

    private void RefreshWfPlanPreview()
    {
        var cfg = TrialConfig();
        string preview;
        bool valid;

        if (_datasetBars > 0)
        {
            try
            {
                var plan = WalkForwardWindowPlanner.Plan(_datasetBars, cfg);
                var first = plan[0];
                preview = $"{plan.Count} window(s) · IS {first.IsEndBar - first.IsStartBar} bars · OOS {first.OosEndBar - first.OosStartBar} bars";
                valid = true;
            }
            catch (WalkForwardPlanException ex)
            {
                preview = ex.Message;
                valid = false;
            }
        }
        else
        {
            var unit = string.Equals(WfLengthUnit, "Percent", StringComparison.OrdinalIgnoreCase) ? "%" : "bars";
            preview = $"{WfWindowMode} · IS {WfIsLength}{unit} · OOS {WfOosLength}{unit} · Step {WfStepLength}{unit} · min {WfMinWindows} window(s)";
            valid = WfIsLength > 0 && WfOosLength > 0 && WfStepLength > 0 && WfMinWindows >= 1;
        }

        WfPlanPreviewText = preview;
        WfIsPlanValid = valid;
        WfPlanInvalidReason = valid ? "" : preview;
    }

    private WalkForwardRunConfig TrialConfig()
    {
        // The planner only reads the windowing fields; Optimization is carried for completeness.
        return new WalkForwardRunConfig(
            WindowMode: WfWindowMode,
            LengthUnit: WfLengthUnit,
            IsLength: WfIsLength,
            OosLength: WfOosLength,
            StepLength: WfStepLength,
            MinWindows: WfMinWindows,
            Objective: WfObjective,
            CarryTopN: WfCarryTopN,
            Backend: McBackend,
            Optimization: new OptimizationRunConfig(
                MaxCombinations: Cap,
                CoarseIterations: Math.Max(10, _config.Settings.OptimizerCoarseIterations),
                RefineIterations: Math.Max(10, _config.Settings.OptimizerRefineIterations),
                RefineTopN: Math.Max(1, _config.Settings.OptimizerRefineTopN),
                MeanBlockSize: McBlockSize,
                StartingCapital: 100_000,
                DrawdownType: "static",
                MaxLossPercent: 10.0,
                MaxDailyLossPercent: 5.0,
                ProfitTargetPercent: 0.0,
                BestDayRulePercent: 50.0,
                TradingPeriodDays: 90));
    }
    [RelayCommand]
    private async Task WalkForwardAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        var queued = _queue.Snapshot();
        if (queued.Count == 0)
        {
            StatusText = "Queue is empty - add data in the Data tab first.";
            return;
        }

        var gateResult = await _queueGate.CheckAsync(queued);
        if (gateResult.Decision == backs2.Core.Data.QueueGateDecision.Warn)
        {
            var proceed = await ConfirmAsync("Raw tick data queued", gateResult.Message, "Continue anyway");
            if (!proceed) { StatusText = "Walk-forward cancelled - raw tick entries still in queue."; return; }
        }

        var distinctSymbols = queued
            .Select(q => q.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctSymbols.Count > 1)
        {
            StatusText = $"Queue spans {distinctSymbols.Count} tickers - walk-forward supports one ticker at a time.";
            return;
        }

        if (!WfIsPlanValid)
        {
            StatusText = "Invalid walk-forward plan: " + WfPlanInvalidReason;
            return;
        }

        var grid = BuildGridParameters();
        if (grid.Count == 0)
        {
            StatusText = "Strategy has no gridable parameters.";
            return;
        }

        var fundedCtx = _accounts.ResolveActiveFundedContext();
        if (fundedCtx == null)
        {
            StatusText = "No active account or funded phase resolved. Set one in the Accounts tab first.";
            return;
        }

        try
        {
            IsBusy = true;
            IsWalkForwardRunning = true;
            WfProgress = 0;
            WfEtaText = "";
            StatusText = "Walk-forward starting...";

            _walkForwardCts = new CancellationTokenSource();
            var ct = _walkForwardCts.Token;

            int tradingPeriodDays = fundedCtx.FundedPhase.TradingPeriodDays ?? 90;

            int savedBatches = 0;
            int totalWindows = 0;

            for (int i = 0; i < queued.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var item = queued[i];
                int idx = i;

                StatusText = $"[{idx + 1}/{queued.Count}] Walk-forward {item.Symbol}@{item.Timeframe} - fetching data...";

                var fetchProgress = new Progress<DataProgress>(p =>
                    StatusText = $"[{idx + 1}/{queued.Count}] {item.Symbol}@{item.Timeframe} - {p.Message}");

                var data = await _data.GetSymbolDataAsync(item.Symbol, item.Timeframe, fetchProgress);
                if (data == null || data.Count == 0)
                {
                    LogManager.Warn(LogTag.Runner, $"walk-forward: no data for {item.Symbol}@{item.Timeframe}, skipping");
                    continue;
                }

                _datasetBars = data.Count;
                RefreshWfPlanPreview();

                var runConfig = new OptimizationRunConfig(
                    MaxCombinations: Cap,
                    CoarseIterations: Math.Max(10, _config.Settings.OptimizerCoarseIterations),
                    RefineIterations: Math.Max(10, _config.Settings.OptimizerRefineIterations),
                    RefineTopN: Math.Max(1, _config.Settings.OptimizerRefineTopN),
                    MeanBlockSize: McBlockSize,
                    StartingCapital: fundedCtx.PlanSize.StartingCapital,
                    DrawdownType: string.IsNullOrWhiteSpace(fundedCtx.FundedPhase.DrawdownType)
                        ? "static"
                        : fundedCtx.FundedPhase.DrawdownType!,
                    MaxLossPercent: fundedCtx.FundedPhase.MaxLossPercent ?? 10.0,
                    MaxDailyLossPercent: fundedCtx.FundedPhase.MaxDailyLossPercent ?? 5.0,
                    ProfitTargetPercent: fundedCtx.FundedPhase.ProfitTargetPercent ?? 0.0,
                    BestDayRulePercent: fundedCtx.FundedPhase.BestDayRulePercent ?? 50.0,
                    TradingPeriodDays: tradingPeriodDays,
                    GpuFactor: _config.Settings.OptimizerGpuFactor);

                var cfg = new WalkForwardRunConfig(
                    WindowMode: WfWindowMode,
                    LengthUnit: WfLengthUnit,
                    IsLength: WfIsLength,
                    OosLength: WfOosLength,
                    StepLength: WfStepLength,
                    MinWindows: WfMinWindows,
                    Objective: WfObjective,
                    CarryTopN: WfCarryTopN,
                    Backend: McBackend,
                    Optimization: runConfig);

                var wfProgress = new Progress<WalkForwardProgress>(p =>
                {
                    WfProgress = p.Fraction * 100.0;
                    WfEtaText = p.EtaSeconds > 0 ? FormatEta(p.EtaSeconds) : "";
                    StatusText =
                        $"[{idx + 1}/{queued.Count}] Walk-forward {item.Symbol}@{item.Timeframe} - " +
                        $"{p.Phase} window {p.WindowIndex + 1}/{p.TotalWindows}";
                });

                var batch = await _walkForwardRunner.RunAsync(
                    SelectedStrategy.Path,
                    SelectedStrategy.Name,
                    item.Symbol,
                    item.Timeframe,
                    $"{item.Symbol}@{item.Timeframe}",
                    data,
                    grid,
                    cfg,
                    wfProgress,
                    ct).ConfigureAwait(false);

                await _walkForwardStore.SaveAsync(batch, ct).ConfigureAwait(false);

                savedBatches++;
                totalWindows += batch.Windows.Length;
                StatusText =
                    $"[{idx + 1}/{queued.Count}] Walk-forward {item.Symbol}@{item.Timeframe} - " +
                    $"saved ({batch.Windows.Length} window(s))";
            }

            StatusText = $"Walk-forward complete - {savedBatches} batch(es), {totalWindows:N0} window(s) total.";
            SelectedSubtabIndex = 2;
            _ = RefreshWalkForwardResultsAsync();
        }
        catch (OperationCanceledException)
        {
            StatusText = "Walk-forward cancelled.";
            WfEtaText = "";
            LogManager.Info(LogTag.Runner, "walk-forward cancelled by user");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, "walk-forward failed", ex);
            StatusText = $"Walk-forward failed: {ex.Message}";
            WfEtaText = "";
        }
        finally
        {
            _walkForwardCts?.Dispose();
            _walkForwardCts = null;
            IsBusy = false;
            IsWalkForwardRunning = false;
        }
    }

    [RelayCommand]
    private void CancelWalkForward()
    {
        _walkForwardCts?.Cancel();
        StatusText = "Walk-forward cancellation requested.";
    }
}
