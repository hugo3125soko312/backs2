using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

public sealed record McBatchRow(
    string Symbol,
    string Timeframe,
    double PRuin,
    double PFunded,
    double PTimeout)
{
    public string Display => $"{Symbol} @ {Timeframe}";
    public string PRuinText => PRuin.ToString("P1");
    public string PFundedText => PFunded.ToString("P1");
    public string PTimeoutText => PTimeout.ToString("P1");
}

public partial class MonteCarloViewModel : ObservableObject
{
    private readonly LastRunStore _lastRun;
    private readonly AccountService _accounts;
    private readonly MonteCarloEngine _engine;

    private AvaPlot? _plot;
    private readonly Dictionary<int, MonteCarloResult> _resultsByIndex = new();
    private IReadOnlyList<OverviewData> _batch = Array.Empty<OverviewData>();

    public ObservableCollection<McStatRow> Stats { get; } = new();
    public ObservableCollection<OverviewData> BatchItems { get; } = new();
    public ObservableCollection<McBatchRow> BatchResults { get; } = new();

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
    [ObservableProperty] private bool _hasResults;

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
        ApplyPlotTheme();
        RenderForSelection();
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

            SelectedBatchItem = _batch.Count > 0 ? _batch[_batch.Count - 1] : null;

            ClearResults();
            RefreshSourceInfo();
        });
    }

    partial void OnSelectedBatchItemChanged(OverviewData? value)
    {
        RefreshSourceInfo();
        RenderForSelection();
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

    private void ClearResults()
    {
        _resultsByIndex.Clear();
        BatchResults.Clear();
        Stats.Clear();
        HasResults = false;
        _plot?.Plot.Clear();
        ApplyPlotTheme();
        _plot?.Refresh();
    }

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

        if (_batch.Count == 0)
        {
            StatusText = "No run data available. Run a strategy in the Runner tab first.";
            return;
        }

        var account = _accounts.Active;
        if (account == null)
        {
            StatusText = "No active account. Set one in the Accounts tab first.";
            return;
        }

        var planSize = _accounts.ResolvePlanSize(account);
        if (planSize == null || planSize.Phases.Count == 0)
        {
            StatusText = "Active account has no resolved phases. Cannot run Monte Carlo.";
            return;
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            ClearResults();

            var funded = planSize.Phases[planSize.Phases.Count - 1];

            double startingCapital = planSize.StartingCapital;
            double maxLossPct = funded.MaxLossPercent ?? 10.0;
            double maxDailyLossPct = funded.MaxDailyLossPercent ?? 5.0;
            double profitTargetPct = funded.ProfitTargetPercent ?? 0.0;
            string drawdownType = string.IsNullOrWhiteSpace(funded.DrawdownType) ? "static" : funded.DrawdownType!;

            int tradingPeriodDays = funded.TradingPeriodDays ?? 90;
            int iterations = Iterations;
            int blockSize = MeanBlockSize;

            int totalRuns = _batch.Count;
            int runIndex = 0;

            var progress = new Progress<int>(done =>
            {
                double perRun = (double)done / iterations;
                Progress = ((runIndex + perRun) / totalRuns) * 100.0;
            });

            for (int i = 0; i < _batch.Count; i++)
            {
                var data = _batch[i];
                runIndex = i;

                if (data.Error != null || data.Trades.Count < 2)
                {
                    StatusText = $"Skipping {data.Symbol}@{data.Timeframe} — insufficient trades.";
                    continue;
                }

                var slices = TimeSliceBuilder.Build(data.Trades, data.SourceData);
                if (slices.Length == 0)
                {
                    StatusText = $"Skipping {data.Symbol}@{data.Timeframe} — no time slices.";
                    continue;
                }

                int targetDurationSlices = ComputeTargetSlices(data, tradingPeriodDays);

                StatusText = $"Running {data.Symbol}@{data.Timeframe} ({i + 1}/{_batch.Count}) — {iterations} simulations...";

                var result = await Task.Run(() => _engine.Run(
                    slices,
                    targetDurationSlices,
                    startingCapital,
                    drawdownType,
                    maxDailyLossPct,
                    maxLossPct,
                    profitTargetPct,
                    expectedBlockSize: blockSize,
                    seed: null,
                    progress: progress));

                _resultsByIndex[i] = result;

                BatchResults.Add(new McBatchRow(
                    data.Symbol,
                    data.Timeframe,
                    result.PRuin,
                    result.PFunded,
                    result.PTimeout));
            }

            HasResults = _resultsByIndex.Count > 0;

            RenderForSelection();

            StatusText = HasResults
                ? $"Done — Monte Carlo ran on {_resultsByIndex.Count} dataset(s)."
                : "No datasets produced a result.";
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

    private static int ComputeTargetSlices(OverviewData data, int tradingPeriodDays)
    {
        if (data.SourceData == null || data.SourceData.Count < 2)
            return tradingPeriodDays;

        long firstMs = new DateTimeOffset(data.SourceData[0].Timestamp).ToUnixTimeMilliseconds();
        long lastMs = new DateTimeOffset(data.SourceData[data.SourceData.Count - 1].Timestamp).ToUnixTimeMilliseconds();
        double spanDays = Math.Max((lastMs - firstMs) / 86_400_000.0, 1.0 / 24.0);
        double slicesPerDay = data.SourceData.Count / spanDays;

        int slices = (int)Math.Round(tradingPeriodDays * slicesPerDay);
        return slices < 1 ? tradingPeriodDays : slices;
    }

    private MonteCarloResult? SelectedResult
    {
        get
        {
            if (SelectedBatchItem == null) return null;
            int idx = BatchItems.IndexOf(SelectedBatchItem);
            if (idx < 0) return null;
            return _resultsByIndex.TryGetValue(idx, out var r) ? r : null;
        }
    }

    private void RenderForSelection()
    {
        var r = SelectedResult;
        if (r == null)
        {
            Stats.Clear();
            _plot?.Plot.Clear();
            ApplyPlotTheme();
            _plot?.Refresh();
            return;
        }

        PopulateStats(r);
        RenderPlots(r);
    }

    private void ApplyPlotTheme()
    {
        if (_plot == null) return;
        var plot = _plot.Plot;
        var labelColor = ScottPlot.Color.FromHex("#888888");

        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.Axes.Color(ScottPlot.Color.FromHex("#333333"));

        plot.Axes.Bottom.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Left.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Right.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Bottom.Label.ForeColor = labelColor;
        plot.Axes.Left.Label.ForeColor = labelColor;
        plot.Axes.Right.Label.ForeColor = labelColor;
    }

    private void PopulateStats(MonteCarloResult r)
    {
        Stats.Clear();

        Stats.Add(new McStatRow("P(ruin) / P(funded) / P(timeout)",
            r.PRuin.ToString("P1"),
            r.PFunded.ToString("P1"),
            r.PTimeout.ToString("P1"),
            "—",
            "—"));

        Stats.Add(new McStatRow("Expected Slices",
            r.ExpectedSlicesToFund.ToString("N1"),
            "—",
            r.ExpectedSlicesToRuin.ToString("N1"),
            "—",
            "—"));

        Stats.Add(new McStatRow("Terminal Balance",
            r.TerminalBalanceMean.ToString("N0"),
            r.TerminalBalanceMedian.ToString("N0"),
            r.TerminalBalanceStdDev.ToString("N0"),
            r.TerminalBalanceP05.ToString("N0"),
            r.TerminalBalanceP95.ToString("N0")));

        Stats.Add(new McStatRow("Max Drawdown %",
            r.MaxDrawdownMean.ToString("0.00") + "%",
            r.MaxDrawdownMedian.ToString("0.00") + "%",
            "—",
            r.MaxDrawdownP05.ToString("0.00") + "%",
            r.MaxDrawdownP95.ToString("0.00") + "%"));
    }

    private void RenderPlots(MonteCarloResult r)
    {
        if (_plot == null) return;
        if (r.TerminalBalances.Length == 0) return;

        var plt = _plot.Plot;
        plt.Clear();

        ApplyPlotTheme();

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(40, r.TerminalBalances);
        var bars = plt.Add.Bars(hist.Bins, hist.Counts);
        bars.Color = ScottPlot.Color.FromHex("#4FC3F7");

        double p05 = r.TerminalBalanceP05;
        double p50 = r.TerminalBalanceMedian;
        double p95 = r.TerminalBalanceP95;
        plt.Add.VerticalLine(p05, 2, ScottPlot.Color.FromHex("#EF5350"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(p50, 1, ScottPlot.Colors.White, ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(p95, 2, ScottPlot.Color.FromHex("#26A69A"), ScottPlot.LinePattern.Solid);

        plt.Axes.AutoScale();

        plt.Title("Terminal Balance Distribution");
        plt.Axes.Bottom.Label.Text = "Terminal Balance";
        plt.Axes.Left.Label.Text = "Frequency";

        ApplyPlotTheme();

        _plot.Refresh();
    }
}