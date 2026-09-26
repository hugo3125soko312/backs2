using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core.Accounts;
using backs2.Core.Overview;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public sealed record McStatRow(
    string Metric,
    string Mean,
    string Median,
    string StdDev,
    string P05,
    string P95);

public partial class MonteCarloViewModel : ObservableObject
{
    private readonly LastRunStore _lastRun;
    private readonly AccountService _accounts;
    private readonly MonteCarloEngine _engine;

    private AvaPlot? _plot;
    private MonteCarloResult? _lastResult;
    private IReadOnlyList<OverviewData> _batch = Array.Empty<OverviewData>();

    public ObservableCollection<McStatRow> Stats { get; } = new();
    public ObservableCollection<OverviewData> BatchItems { get; } = new();

    [ObservableProperty] private OverviewData? _selectedBatchItem;

    [ObservableProperty] private int _iterations = 1000;
    [ObservableProperty] private int _meanBlockSize = 5;
    [ObservableProperty] private double _ruinThresholdPercent = 50;
    [ObservableProperty] private int _progressIntervalPercent = 10;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _statusText = "Ready. Run a strategy first, then click Run Monte Carlo.";
    [ObservableProperty] private string _activeAccountName = "(none)";
    [ObservableProperty] private string _sourceInfo = "No run data yet.";
    [ObservableProperty] private bool _hasRunData;
    [ObservableProperty] private bool _hasBatch;

    public MonteCarloViewModel(
        LastRunStore lastRun,
        AccountService accounts,
        MonteCarloEngine engine)
    {
        _lastRun = lastRun;
        _accounts = accounts;
        _engine = engine;

        _lastRun.Changed += OnLastRunChanged;
        _accounts.ActiveChanged += OnActiveChanged;

        ApplyAccountDefaults();

        if (_lastRun.Batch.Count > 0)
            OnLastRunChanged(this, _lastRun.Batch);
    }

    public void SetPlotControl(AvaPlot plot)
    {
        _plot = plot;
        if (_lastResult != null) RenderPlots(_lastResult);
    }

    private void OnLastRunChanged(object? sender, IReadOnlyList<OverviewData> batch)
    {
        Dispatcher.UIThread.Post(() =>
        {
            _batch = batch ?? Array.Empty<OverviewData>();

            BatchItems.Clear();
            foreach (var item in _batch)
                BatchItems.Add(item);

            HasBatch = _batch.Count > 1;

            // Select the most recent entry by default.
            SelectedBatchItem = _batch.Count > 0 ? _batch[_batch.Count - 1] : null;

            // Stale results — the trades changed.
            _lastResult = null;
            Stats.Clear();
            _plot?.Plot.Clear();
            _plot?.Refresh();

            RefreshSourceInfo();
        });
    }

    partial void OnSelectedBatchItemChanged(OverviewData? value)
    {
        _lastResult = null;
        Stats.Clear();
        _plot?.Plot.Clear();
        _plot?.Refresh();
        RefreshSourceInfo();
    }

    private void OnActiveChanged(object? sender, AccountDescriptor? descriptor)
    {
        Dispatcher.UIThread.Post(() => ApplyAccountDefaults());
    }

    private void ApplyAccountDefaults()
    {
        var active = _accounts.Active;
        ActiveAccountName = active == null ? "(none)" : $"{active.Firm} · {active.Plan} · {active.Size}";

        if (active != null)
        {
            var planSize = _accounts.ResolvePlanSize(active);
            if (planSize != null && planSize.Phases.Count > 0)
            {
                var funded = planSize.Phases[planSize.Phases.Count - 1];
                if (funded.MaxLossPercent.HasValue)
                    RuinThresholdPercent = funded.MaxLossPercent.Value;
            }
        }
    }

    private OverviewData? CurrentData => SelectedBatchItem ?? _lastRun.Current;

    private void RefreshSourceInfo()
    {
        var data = CurrentData;
        if (data == null)
        {
            SourceInfo = "No run data yet.";
            HasRunData = false;
            return;
        }
    
        if (data.Error != null)
        {
            SourceInfo = $"Run failed — {data.Error}";
            HasRunData = false;
            return;
        }
    
        int idx = BatchItems.IndexOf(data);
        var prefix = HasBatch && BatchItems.Count > 1 && idx >= 0
            ? $"({idx + 1}/{BatchItems.Count}) "
            : "";
    
        SourceInfo = $"{prefix}{data.StrategyName} · {data.Symbol} @ {data.Timeframe} · {data.Trades.Count} trades";
        HasRunData = data.Trades.Count >= 2;
    }

    [RelayCommand]
    private async Task RunAsync()
    {
        if (IsBusy) return;

        var data = CurrentData;
        if (data == null || data.Trades.Count < 2)
        {
            StatusText = "No run data available. Run a strategy in the Runner tab first.";
            return;
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            StatusText = $"Running {Iterations} simulations (block size {MeanBlockSize})...";

            var account = _accounts.Active;
            double startingCapital = 10000;
            if (account != null)
            {
                var planSize = _accounts.ResolvePlanSize(account);
                if (planSize != null) startingCapital = planSize.StartingCapital;
            }

            int iterations = Iterations;
            int blockSize = MeanBlockSize;
            double ruinPct = RuinThresholdPercent;
            var trades = data.Trades;
            var duration = data.Duration;

            var progress = new Progress<int>(done =>
            {
                Progress = (double)done / iterations * 100.0;
            });

            var result = await Task.Run(() => _engine.Run(
                trades,
                duration,
                iterations: iterations,
                meanBlockSize: blockSize,
                startingCapital: startingCapital,
                ruinThresholdPercent: ruinPct,
                seed: null,
                progress: progress));

            _lastResult = result;
            PopulateStats(result);
            RenderPlots(result);

            StatusText = $"Done — {result.Iterations} simulations. Risk of Ruin: {result.RiskOfRuin:P2}";
        }
        catch (Exception ex)
        {
            StatusText = $"Monte Carlo failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
            Progress = 100;
        }
    }

    private void PopulateStats(MonteCarloResult r)
    {
        Stats.Clear();

        Stats.Add(new McStatRow("Net Profit",
            FormatMoney(r.NetProfitMean),
            FormatMoney(r.NetProfitMedian),
            FormatMoney(r.NetProfitStdDev),
            FormatMoney(r.NetProfitP05),
            FormatMoney(r.NetProfitP95)));

        Stats.Add(new McStatRow("Max Drawdown %",
            FormatPercent(r.MaxDrawdownMean),
            FormatPercent(r.MaxDrawdownMedian),
            "—",
            FormatPercent(r.MaxDrawdownP05),
            FormatPercent(r.MaxDrawdownP95)));

        Stats.Add(new McStatRow("Annual Return %",
            FormatPercent(r.AnnualReturnMean),
            "—",
            "—",
            FormatPercent(r.AnnualReturnP05),
            FormatPercent(r.AnnualReturnP95)));

        Stats.Add(new McStatRow("Sharpe",
            r.SharpeMean.ToString("0.00"),
            "—",
            "—",
            r.SharpeP05.ToString("0.00"),
            r.SharpeP95.ToString("0.00")));

        Stats.Add(new McStatRow("Profit Factor",
            r.ProfitFactorMean.ToString("0.00"),
            "—",
            "—",
            r.ProfitFactorP05.ToString("0.00"),
            r.ProfitFactorP95.ToString("0.00")));

        Stats.Add(new McStatRow("Max Consecutive Losses",
            r.MaxConsecutiveLossesMean.ToString("0.0"),
            "—",
            "—",
            "—",
            r.MaxConsecutiveLossesMax.ToString()));
    }

    private void RenderPlots(MonteCarloResult r)
    {
        if (_plot == null) return;

        var plt = _plot.Plot;
        plt.Clear();

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(40, r.NetProfits);
        var bars = plt.Add.Bars(hist.Bins, hist.Counts);
        bars.Color = ScottPlot.Color.FromHex("#4FC3F7");

        double p05 = r.NetProfitP05;
        double p50 = r.NetProfitMedian;
        double p95 = r.NetProfitP95;
        plt.Add.VerticalLine(p05, 2, ScottPlot.Color.FromHex("#EF5350"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(p50, 1, ScottPlot.Colors.White, ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(p95, 2, ScottPlot.Color.FromHex("#26A69A"), ScottPlot.LinePattern.Solid);

        plt.Title("Net Profit Distribution");
        plt.Axes.Bottom.Label.Text = "Final Equity";
        plt.Axes.Left.Label.Text = "Frequency";

        _plot.Refresh();
    }

    private static string FormatMoney(double v) => v.ToString("N0");
    private static string FormatPercent(double v) => v.ToString("0.00") + "%";
}