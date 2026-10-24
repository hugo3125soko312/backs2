using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Optimizer;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    [RelayCommand]
    private async Task OptimizeAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "No strategy selected.";
            return;
        }

        var queued = _queue.Snapshot();
        if (queued.Count == 0)
        {
            StatusText = "Queue is empty. Add data in the Data tab first.";
            return;
        }

        var distinctSymbols = queued
            .Select(q => q.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (distinctSymbols.Count > 1)
        {
            StatusText = $"Optimizer supports one ticker at a time - queue contains {distinctSymbols.Count}: {string.Join(", ", distinctSymbols)}. Narrow the queue in the Data tab and try again.";
            return;
        }

        if (IsBusy) return;

        var grid = BuildGridParameters();
        if (grid.Count == 0)
        {
            StatusText = "Strategy has no parameters to optimize.";
            return;
        }

        if (ExceedsCap)
        {
            var confirmed = await ConfirmAsync(
                "Grid will be sampled",
                $"Your grid has {TotalCombinations:N0} combinations but the cap is {Cap:N0}. The optimizer will randomly sample {Cap:N0} of them. Continue?",
                "Yes, sample");
            if (!confirmed)
            {
                StatusText = "Optimization cancelled.";
                return;
            }
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
            IsOptimizing = true;
            Progress = 0;
            IsProgressVisible = true;
            IsProgressIndeterminate = false;
            EtaText = "";

            _optimizeCts = new CancellationTokenSource();
            var ct = _optimizeCts.Token;

            int tradingPeriodDays = fundedCtx.FundedPhase.TradingPeriodDays ?? 90;

            int totalDatasets = queued.Count;
            int savedBatches = 0;
            int totalVersions = 0;

            for (int i = 0; i < totalDatasets; i++)
            {
                ct.ThrowIfCancellationRequested();

                var item = queued[i];
                int idx = i;

                StatusText = $"[{idx + 1}/{totalDatasets}] Optimize {item.Symbol}@{item.Timeframe} - fetching data...";

                var fetchProgress = new Progress<DataProgress>(p =>
                {
                    StatusText = $"[{idx + 1}/{totalDatasets}] {item.Symbol}@{item.Timeframe} - {p.Message}";
                });

                var data = await _data.GetSymbolDataAsync(item.Symbol, item.Timeframe, fetchProgress);
                if (data == null || data.Count == 0)
                {
                    LogManager.Warn(LogTag.Runner, $"optimize: no data for {item.Symbol}@{item.Timeframe}, skipping");
                    continue;
                }

                var optProgress = new ThrottledProgress<OptimizationProgress>(p =>
                {
                    double batchFraction = p.Fraction;
                    Progress = ((idx + batchFraction) / totalDatasets) * 100.0;

                    EtaText = p.EtaSeconds.HasValue ? FormatEta(p.EtaSeconds.Value) : "";

                    StatusText = $"[{idx + 1}/{totalDatasets}] Optimize {item.Symbol}@{item.Timeframe} - {p.Phase} {p.Completed}/{p.Total}";
                },
                minIntervalMs: 100,
                flushImmediately: p => p.Phase == "Done" || p.Completed == p.Total);

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

                StatusText = $"[{idx + 1}/{totalDatasets}] Optimize {item.Symbol}@{item.Timeframe} - starting...";

                var batch = await _optimizer.RunAsync(
                    SelectedStrategy.Path,
                    SelectedStrategy.Name,
                    item.Symbol,
                    item.Timeframe,
                    data,
                    grid,
                    runConfig,
                    optProgress,
                    ct);

                savedBatches++;
                totalVersions += batch.Versions.Length;

                LogManager.Info(LogTag.Runner,
                    $"optimize: batch {batch.Id} saved ({batch.Versions.Length} version(s))");

                Progress = ((idx + 1.0) / totalDatasets) * 100.0;
            }

            StatusText = $"Optimization complete - {savedBatches} batch(es), {totalVersions:N0} version(s) total.";
            EtaText = "";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Optimization cancelled.";
            EtaText = "";
            LogManager.Info(LogTag.Runner, "optimization cancelled by user");
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, "optimization failed", ex);
            StatusText = $"Optimization failed: {ex.Message}";
            EtaText = "";
        }
        finally
        {
            _optimizeCts?.Dispose();
            _optimizeCts = null;
            IsBusy = false;
            IsOptimizing = false;
            IsProgressVisible = false;
            IsProgressIndeterminate = false;
        }
    }

    [RelayCommand]
    private void CancelOptimize()
    {
        _optimizeCts?.Cancel();
        StatusText = "Cancellation requested - will stop after the current batch.";
    }

    private List<GridParameter> BuildGridParameters()
    {
        var list = new List<GridParameter>(Parameters.Count);
        foreach (var p in Parameters)
        {
            list.Add(new GridParameter(
                Name: p.Name,
                Type: p.Type,
                FixedValue: p.EffectiveValue,
                Min: p.MinValue,
                Max: p.MaxValue,
                Increment: p.IncrementValue,
                Options: p.Options));
        }
        return list;
    }

    private static string FormatEta(double seconds)
    {
        if (seconds <= 0 || double.IsNaN(seconds) || double.IsInfinity(seconds)) return "";
        if (seconds < 60) return $"ETA {seconds:F0}s";
        if (seconds < 3600)
        {
            int m = (int)(seconds / 60);
            int s = (int)(seconds % 60);
            return $"ETA {m}m {s}s";
        }
        int h = (int)(seconds / 3600);
        int rem = (int)((seconds - h * 3600) / 60);
        return $"ETA {h}h {rem}m";
    }
}