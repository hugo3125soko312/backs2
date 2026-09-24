using System;
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

    public OverviewViewModel(LastRunStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;

        if (_store.Current != null)
            OnStoreChanged(this, _store.Current);
    }

    private void OnStoreChanged(object? sender, OverviewData data)
    {
        Dispatcher.UIThread.Post(() =>
        {
            StrategyName = data.StrategyName;
            Symbol = $"{data.Symbol} @ {data.Timeframe}";
            HasData = true;

            if (data.Error != null)
            {
                RunInfo = $"FAIL — {data.Error}";
                StatusText = "Last run failed.";
                return;
            }

            RunInfo = $"{data.Trades.Count} trades — {data.Duration.TotalMilliseconds:F0} ms — {data.RunUtc:HH:mm:ss}";
            StatusText = $"Loaded {data.StrategyName} ({data.Trades.Count} trades).";
        });
    }
}