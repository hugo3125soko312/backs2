using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Overview;
using backs2.Core.Runner;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    [RelayCommand]
    private async Task RunAsync()
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
            StatusText = $"Runner supports one ticker at a time - queue contains {distinctSymbols.Count}: {string.Join(", ", distinctSymbols)}. Narrow the queue in the Data tab and try again.";
            return;
        }

        if (IsBusy) return;

        if (queued.Count > 1)
        {
            var confirmed = await ConfirmAsync(
                "Multiple Datasets",
                $"You have selected {queued.Count} timeframes in the queue. Running will execute the strategy on all {queued.Count} timeframes sequentially.",
                "Yes, continue");
            if (!confirmed)
            {
                StatusText = "Batch run cancelled.";
                return;
            }
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            IsProgressVisible = true;
            IsProgressIndeterminate = false;

            var paramValues = new Dictionary<string, string>();
            foreach (var p in Parameters)
                paramValues[p.Name] = p.EffectiveValue;

            var effectiveTimeframes = queued.Select(q => q.Timeframe).ToList();
            StatusText = $"Running {SelectedStrategy.Name} on {distinctSymbols[0]} - {effectiveTimeframes.Count} timeframe(s): {string.Join(", ", effectiveTimeframes)}";

            var batchResults = new List<OverviewData>();
            int totalDatasets = queued.Count;

            for (int i = 0; i < totalDatasets; i++)
            {
                var item = queued[i];
                int datasetIndex = i;

                var progress = new Progress<DataProgress>(p =>
                {
                    double datasetFraction = p.IsDeterminate ? p.Percent / 100.0 : 0.0;
                    Progress = ((datasetIndex + datasetFraction) / totalDatasets) * 100.0;

                    if (p.IsDeterminate) IsProgressIndeterminate = false;
                    else if (p.Percent >= 100) IsProgressIndeterminate = false;
                    else IsProgressIndeterminate = true;

                    StatusText = $"[{datasetIndex + 1}/{totalDatasets}] {item.Symbol}@{item.Timeframe} - {p.Message}";
                });

                var data = await _data.GetSymbolDataAsync(item.Symbol, item.Timeframe, progress);
                if (data == null || data.Count == 0)
                {
                    LogManager.Warn(LogTag.Runner, $"no data for {item.Symbol}@{item.Timeframe}, skipping");
                    batchResults.Add(new OverviewData(
                        StrategyName: SelectedStrategy.Name,
                        StrategyPath: SelectedStrategy.Path,
                        Symbol: item.Symbol,
                        Timeframe: item.Timeframe,
                        RunUtc: DateTime.UtcNow,
                        Duration: TimeSpan.Zero,
                        Trades: Array.Empty<Trade>(),
                        SourceData: Array.Empty<Core.Models.Kline>(),
                        Error: "No data available."));
                    continue;
                }

                IsProgressIndeterminate = false;
                StatusText = $"[{datasetIndex + 1}/{totalDatasets}] Running strategy on {item.Symbol}@{item.Timeframe}...";

                var result = await _strat.RunBacktestFromFileAsync(
                    SelectedStrategy.Path,
                    data,
                    paramValues);

                batchResults.Add(new OverviewData(
                    StrategyName: SelectedStrategy.Name,
                    StrategyPath: SelectedStrategy.Path,
                    Symbol: item.Symbol,
                    Timeframe: item.Timeframe,
                    RunUtc: DateTime.UtcNow,
                    Duration: result.Duration,
                    Trades: result.Trades,
                    SourceData: data,
                    Error: result.Success ? null : result.Error));

                RunCompleted?.Invoke(this, result);
                Progress = ((datasetIndex + 1.0) / totalDatasets) * 100.0;
            }

            _lastRun.PublishBatch(batchResults);

            int ok = batchResults.Count(r => r.Error == null);
            int total = batchResults.Count;
            int totalTrades = batchResults.Sum(r => r.Trades.Count);
            StatusText = $"Batch complete - {ok}/{total} succeeded, {totalTrades} trades total.";
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Runner, "batch run failed", ex);
            StatusText = $"Batch failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            IsProgressVisible = false;
            IsProgressIndeterminate = false;
        }
    }
}