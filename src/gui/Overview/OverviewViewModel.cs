using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Overview;

namespace backs2.ViewModels;

public sealed record OverviewRow(
    string Symbol,
    string Timeframe,
    int Trades,
    double NetPnl,
    double WinRatePercent,
    long DurationMs,
    string Status)
{
    public string SymbolDisplay => $"{Symbol} @ {Timeframe}";
    public string TradesText => Trades.ToString("N0");
    public string NetPnlText => NetPnl >= 0 ? "+" + NetPnl.ToString("N2") : NetPnl.ToString("N2");
    public string WinRateText => WinRatePercent.ToString("0.0") + "%";
    public string DurationText => $"{DurationMs} ms";
}

public sealed record OverviewAggregate(
    int TotalTrades,
    double TotalNetPnl,
    double WeightedWinRatePercent,
    long TotalDurationMs)
{
    public string TotalTradesText => TotalTrades.ToString("N0");
    public string TotalNetPnlText =>
        TotalNetPnl >= 0 ? "+" + TotalNetPnl.ToString("N2") : TotalNetPnl.ToString("N2");
    public string WeightedWinRateText => WeightedWinRatePercent.ToString("0.0") + "%";
    public string TotalDurationText => $"{TotalDurationMs} ms";
}

public partial class OverviewViewModel : ObservableObject
{
    private readonly LastRunStore _store;

    public ObservableCollection<OverviewRow> Rows { get; } = new();

    [ObservableProperty] private string _strategyName = "—";
    [ObservableProperty] private string _symbol = "—";
    [ObservableProperty] private string _runInfo = "No run yet.";
    [ObservableProperty] private string _statusText = "Run a strategy to populate Overview.";
    [ObservableProperty] private bool _hasData;
    [ObservableProperty] private string _batchSummary = "";
    [ObservableProperty] private OverviewAggregate? _aggregate;
    [ObservableProperty] private bool _hasRows;

    public OverviewViewModel(LastRunStore store)
    {
        _store = store;
        _store.Changed += OnStoreChanged;

        if (_store.Batch.Count > 0)
            OnStoreChanged(this, _store.Batch);
    }

    private void OnStoreChanged(object? sender, IReadOnlyList<OverviewData> batch)
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            Rows.Clear();

            if (batch == null || batch.Count == 0)
            {
                HasData = false;
                HasRows = false;
                Aggregate = null;
                BatchSummary = "";
                RunInfo = "No run yet.";
                StatusText = "Run a strategy to populate Overview.";
                return;
            }

            var latest = batch[batch.Count - 1];

            StrategyName = latest.StrategyName;
            Symbol = $"{latest.Symbol} @ {latest.Timeframe}";
            HasData = true;

            int ok = 0;
            int totalTrades = 0;
            double totalNetPnl = 0;
            double winWeighted = 0;
            long totalDurationMs = 0;

            for (int i = 0; i < batch.Count; i++)
            {
                var d = batch[i];
                bool failed = d.Error != null;

                if (!failed) ok++;

                int tradeCount = d.Trades?.Count ?? 0;
                double netPnl = 0;
                int wins = 0;

                if (!failed && d.Trades != null && tradeCount > 0)
                {
                    for (int t = 0; t < tradeCount; t++)
                    {
                        double pnl = d.Trades[t].PnL;
                        netPnl += pnl;
                        if (pnl > 0) wins++;
                    }
                }

                double winRate = tradeCount > 0 ? (double)wins / tradeCount * 100.0 : 0.0;
                long durationMs = (long)Math.Round(d.Duration.TotalMilliseconds);

                Rows.Add(new OverviewRow(
                    Symbol: d.Symbol,
                    Timeframe: d.Timeframe,
                    Trades: tradeCount,
                    NetPnl: netPnl,
                    WinRatePercent: winRate,
                    DurationMs: durationMs,
                    Status: failed ? "FAIL" : "OK"));

                totalTrades += tradeCount;
                totalNetPnl += netPnl;
                winWeighted += winRate * tradeCount;
                totalDurationMs += durationMs;
            }

            HasRows = Rows.Count > 0;

            Aggregate = new OverviewAggregate(
                TotalTrades: totalTrades,
                TotalNetPnl: totalNetPnl,
                WeightedWinRatePercent: totalTrades > 0 ? winWeighted / totalTrades : 0.0,
                TotalDurationMs: totalDurationMs);

            if (batch.Count > 1)
            {
                int total = batch.Count;
                BatchSummary = $"Batch run — {ok}/{total} succeeded, {totalTrades} trades across {total} dataset(s).";
            }
            else
            {
                BatchSummary = $"Single run — {ok}/1 succeeded, {totalTrades} trades.";
            }

            if (latest.Error != null)
            {
                RunInfo = $"FAIL — {latest.Error}";
                StatusText = "Last run failed.";
                return;
            }

            RunInfo = $"{latest.Trades.Count} trades — {latest.Duration.TotalMilliseconds:F0} ms — {latest.RunUtc:HH:mm:ss}";
            StatusText = $"Loaded {latest.StrategyName} ({totalTrades} trades across {batch.Count} dataset(s)).";
        });
    }
}
