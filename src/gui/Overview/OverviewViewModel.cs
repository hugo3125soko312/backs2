using System;
using System.Linq;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Overview;

namespace backs2.ViewModels;

public partial class OverviewViewModel : ObservableObject
{
    private readonly LastRunStore _store;

    [ObservableProperty] private string _strategyName = "—";
    [ObservableProperty] private string _symbol = "—";
    [ObservableProperty] private string _runInfo = "No run yet.";
    [ObservableProperty] private string _statusText = "Run a strategy to populate Overview.";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _batchSummary = "";

    public OverviewViewModel(LastRunStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;

        if (_store.Batch.Count > 0)
            OnStoreChanged(this, _store.Batch);
    }

    private void OnStoreChanged(object? sender, System.Collections.Generic.IReadOnlyList<OverviewData> batch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (batch == null || batch.Count == 0)
            {
                HasData = false;
                BatchSummary = "";
                RunInfo = "No run yet.";
                return;
            }

            var latest = batch[batch.Count - 1];

            StrategyName = latest.StrategyName;
            Symbol = $"{latest.Symbol} @ {latest.Timeframe}";
            HasData = true;

            if (batch.Count > 1)
            {
                int ok = batch.Count(r => r.Error == null);
                int total = batch.Count;
                int trades = batch.Sum(r => r.Trades.Count);
                BatchSummary = $"Batch run — {ok}/{total} succeeded, {trades} trades total.";
            }
            else
            {
                BatchSummary = "";
            }

            if (latest.Error != null)
            {
                RunInfo = $"FAIL — {latest.Error}";
                StatusText = "Last run failed.";
                return;
            }

            RunInfo = $"{latest.Trades.Count} trades — {latest.Duration.TotalMilliseconds:F0} ms — {latest.RunUtc:HH:mm:ss}";
            StatusText = $"Loaded {latest.StrategyName} ({latest.Trades.Count} trades).";
        });
    }
}