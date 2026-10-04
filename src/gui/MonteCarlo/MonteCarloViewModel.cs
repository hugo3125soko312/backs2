using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core.Accounts;
using backs2.Core.Analysis;
using backs2.Core.Compute;
using backs2.Core.Overview;
using backs2.Core.Runner;
using backs2.ViewModels.Messages;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public enum McPlotMode
{
    TerminalBalance,
    MaxDrawdown,
    Cluster
}

public sealed record PlotModeOption(McPlotMode Mode, string Label);

public sealed record McKpiCard(string Label, string Value, string Subtitle, IBrush ValueBrush);

public sealed record McStatLine(string Label, string Value, IBrush? ValueBrush = null);

public sealed record McStatSection(string Header, IReadOnlyList<McStatLine> Lines);

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
    private const int MaxCurvesToPlot = 200;
    private const int MaxPointsPerCurve = 1000;

    private static readonly ScottPlot.Color[] ClusterPalette =
    {
        ScottPlot.Color.FromHex("#4FC3F7"),
        ScottPlot.Color.FromHex("#EF5350"),
        ScottPlot.Color.FromHex("#FFB74D"),
        ScottPlot.Color.FromHex("#AB47BC"),
        ScottPlot.Color.FromHex("#26A69A"),
        ScottPlot.Color.FromHex("#FF7043"),
        ScottPlot.Color.FromHex("#66BB6A"),
        ScottPlot.Color.FromHex("#EC407A")
    };

    private static readonly IBrush ToneNeutral = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush ToneDim     = new SolidColorBrush(Color.Parse("#7A7A7A"));
    private static readonly IBrush ToneWarn    = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush ToneGood    = new SolidColorBrush(Color.Parse("#26A69A"));
    private static readonly IBrush ToneAmber   = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush ToneAccent  = new SolidColorBrush(Color.Parse("#4FC3F7"));

    private readonly LastRunStore _lastRun;
    private readonly AccountService _accounts;
    private readonly MonteCarloEngine _engine;
    private readonly ComputeWorkerManager _worker;

    private AvaPlot? _histPlot;
    private AvaPlot? _curvesPlot;
    private AvaPlot? _clusterPlot;

    private MonteCarloResult? _histRenderedFrom;
    private McPlotMode? _histRenderedMode;
    private MonteCarloResult? _curvesRenderedFrom;
    private MonteCarloResult? _statsRenderedFrom;
    private OverviewData? _clusterRenderedFrom;

    // Cluster plot state for hit-testing. Rebuilt on each render.
    private OverviewData? _clusterData;
    private List<ClusterPoint>? _clusterPoints;
    private List<ClusterCentroid>? _clusterCentroids;

    private sealed record ClusterPoint(double X, double Y, int ClusterId, Trade Trade);
    private sealed record ClusterCentroid(double X, double Y, int ClusterId, int Size);

    private readonly Dictionary<int, MonteCarloResult> _resultsByIndex = new();
    private IReadOnlyList<OverviewData> _batch = Array.Empty<OverviewData>();

    private double _lastMaxLossPercent = 10.0;

    public ObservableCollection<McKpiCard> Kpis { get; } = new();
    public ObservableCollection<McStatSection> Stats { get; } = new();
    public ObservableCollection<OverviewData> BatchItems { get; } = new();
    public ObservableCollection<McBatchRow> BatchResults { get; } = new();

    public IReadOnlyList<PlotModeOption> PlotModes { get; } = new[]
    {
        new PlotModeOption(McPlotMode.TerminalBalance, "Terminal Balance"),
        new PlotModeOption(McPlotMode.MaxDrawdown, "Max Drawdown"),
        new PlotModeOption(McPlotMode.Cluster, "Trade Clusters")
    };

    [ObservableProperty] private OverviewData? _selectedBatchItem;

    [ObservableProperty] private int _iterations = 1000;
    [ObservableProperty] private int _meanBlockSize = 5;
    [ObservableProperty] private double _ruinThresholdPercent = 50;
    [ObservableProperty] private bool _showEquityCurves;
    [ObservableProperty] private bool _isSettingsExpanded = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SelectedPlotMode))]
    [NotifyPropertyChangedFor(nameof(IsHistogramVisible))]
    [NotifyPropertyChangedFor(nameof(IsCurvesVisible))]
    [NotifyPropertyChangedFor(nameof(IsClusterVisible))]
    private PlotModeOption _selectedPlotModeOption;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsHistogramVisible))]
    [NotifyPropertyChangedFor(nameof(IsCurvesVisible))]
    [NotifyPropertyChangedFor(nameof(IsClusterVisible))]
    private bool _showCurvesDummyFlag; // reserved; the real flag is ShowEquityCurves

    public McPlotMode SelectedPlotMode => SelectedPlotModeOption.Mode;

    public bool IsHistogramVisible => !ShowEquityCurves && SelectedPlotMode != McPlotMode.Cluster;
    public bool IsCurvesVisible    => ShowEquityCurves;
    public bool IsClusterVisible   => !ShowEquityCurves && SelectedPlotMode == McPlotMode.Cluster;

    [ObservableProperty] private string _curveMemoryLabel = "";

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
        MonteCarloEngine engine,
        ComputeWorkerManager worker)
    {
        _lastRun = lastRun;
        _accounts = accounts;
        _engine = engine;
        _worker = worker;

        _selectedPlotModeOption = PlotModes[0];

        _lastRun.Changed += OnLastRunChanged;
        _accounts.ActiveChanged += OnActiveChanged;

        ApplyAccountDefaults();

        if (_lastRun.Batch.Count > 0)
            OnLastRunChanged(this, _lastRun.Batch);
    }

    public void SetHistogramPlotControl(AvaPlot plot)
    {
        _histPlot = plot;
        ApplyPlotTheme(_histPlot);
        EnsureVisiblePlotRendered();
    }

    public void SetCurvesPlotControl(AvaPlot plot)
    {
        _curvesPlot = plot;
        ApplyPlotTheme(_curvesPlot);
        EnsureVisiblePlotRendered();
    }

    public void SetClusterPlotControl(AvaPlot plot)
    {
        _clusterPlot = plot;
        ApplyPlotTheme(_clusterPlot);
        EnsureVisiblePlotRendered();
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
        InvalidateAll();
        RefreshSourceInfo();
        RefreshForSelectedResult();
    }

    partial void OnShowEquityCurvesChanged(bool value)
    {
        OnPropertyChanged(nameof(IsHistogramVisible));
        OnPropertyChanged(nameof(IsCurvesVisible));
        OnPropertyChanged(nameof(IsClusterVisible));
        EnsureVisiblePlotRendered();
    }

    partial void OnSelectedPlotModeOptionChanged(PlotModeOption value)
    {
        _histRenderedFrom = null;
        _histRenderedMode = null;
        _clusterRenderedFrom = null;
        EnsureVisiblePlotRendered();
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
                {
                    RuinThresholdPercent = funded.MaxLossPercent.Value;
                    _lastMaxLossPercent = funded.MaxLossPercent.Value;
                }
            }
        }
    }

    private OverviewData? CurrentData => SelectedBatchItem ?? _lastRun.Current;

    private void InvalidateAll()
    {
        _histRenderedFrom = null;
        _histRenderedMode = null;
        _curvesRenderedFrom = null;
        _statsRenderedFrom = null;
        _clusterRenderedFrom = null;
        _clusterData = null;
        _clusterPoints = null;
        _clusterCentroids = null;
    }

    private void ClearResults()
    {
        _resultsByIndex.Clear();
        BatchResults.Clear();
        Kpis.Clear();
        Stats.Clear();
        HasResults = false;
        CurveMemoryLabel = "";

        InvalidateAll();

        _histPlot?.Plot.Clear();
        _curvesPlot?.Plot.Clear();
        _clusterPlot?.Plot.Clear();
        ApplyPlotTheme(_histPlot);
        ApplyPlotTheme(_curvesPlot);
        ApplyPlotTheme(_clusterPlot);
        _histPlot?.Refresh();
        _curvesPlot?.Refresh();
        _clusterPlot?.Refresh();
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
    private async Task TestWorkerAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Progress = 0;
        StatusText = "Worker selftest — spawning process...";

        try
        {
            var config = new JobConfig(
                ScriptPath: "",
                CachePath: "",
                Params: new Dictionary<string, string>(),
                MemoryLimitMb: 512);

            var progress = new Progress<JobProgress>(p =>
            {
                Progress = p.PercentComplete;
                StatusText = $"Worker selftest — {p.Phase} {p.SlicesElapsed}/{p.TotalSlices} ({p.PercentComplete:F0}%)";
            });

            var result = await _worker.RunJobAsync(config, progress, CancellationToken.None);

            Progress = 100;
            StatusText = $"Worker selftest OK — trades={result.TradeCount}, \"{result.Summary}\"";
        }
        catch (Exception ex)
        {
            StatusText = $"Worker selftest FAILED — {ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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
            double bestDayRulePct = funded.BestDayRulePercent ?? 50.0;
            string drawdownType = string.IsNullOrWhiteSpace(funded.DrawdownType) ? "static" : funded.DrawdownType!;

            _lastMaxLossPercent = maxLossPct;

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
                    iterations: iterations,
                    expectedBlockSize: blockSize,
                    bestDayRulePercent: bestDayRulePct,
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

            InvalidateAll();
            RefreshForSelectedResult();

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

    private void RefreshForSelectedResult()
    {
        var r = SelectedResult;

        if (r == null && SelectedPlotMode != McPlotMode.Cluster)
        {
            Kpis.Clear();
            Stats.Clear();
            CurveMemoryLabel = "";
            _statsRenderedFrom = null;
            EnsureVisiblePlotRendered();
            return;
        }

        if (r != null && _statsRenderedFrom != r)
        {
            PopulateStats(r);
            _statsRenderedFrom = r;
        }

        EnsureVisiblePlotRendered();
    }

    private void EnsureVisiblePlotRendered()
    {
        if (ShowEquityCurves)
        {
            var r = SelectedResult;
            if (r == null || _curvesPlot == null) return;
            if (_curvesRenderedFrom == r) return;
            RenderEquityCurves(r);
            _curvesRenderedFrom = r;
            return;
        }

        if (SelectedPlotMode == McPlotMode.Cluster)
        {
            var data = CurrentData;
            if (data == null || _clusterPlot == null) return;
            if (_clusterRenderedFrom == data) return;
            RenderClusterPlot(data);
            return;
        }

        var histR = SelectedResult;
        if (histR == null || _histPlot == null) return;
        if (_histRenderedFrom == histR && _histRenderedMode == SelectedPlotMode) return;

        if (SelectedPlotMode == McPlotMode.MaxDrawdown)
            RenderMaxDdHistogram(histR);
        else
            RenderPlots(histR);

        _histRenderedFrom = histR;
        _histRenderedMode = SelectedPlotMode;
    }

    private static void ApplyPlotTheme(AvaPlot? plot)
    {
        if (plot == null) return;
        var plt = plot.Plot;

        var labelColor = ScottPlot.Color.FromHex("#C8C8C8");
        var axisColor  = ScottPlot.Color.FromHex("#707070");

        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#0F0F0F");
        plt.DataBackground.Color   = ScottPlot.Color.FromHex("#0F0F0F");

        plt.Axes.Color(axisColor);
        plt.Axes.Bottom.TickLabelStyle.ForeColor = labelColor;
        plt.Axes.Left.TickLabelStyle.ForeColor   = labelColor;
        plt.Axes.Right.TickLabelStyle.ForeColor  = labelColor;
        plt.Axes.Bottom.Label.ForeColor = labelColor;
        plt.Axes.Left.Label.ForeColor   = labelColor;
        plt.Axes.Right.Label.ForeColor  = labelColor;

        plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#262626");
        plt.Grid.MinorLineColor = ScottPlot.Color.FromHex("#1A1A1A");
    }

    private void PopulateStats(MonteCarloResult r)
    {
        Kpis.Clear();
        Stats.Clear();

        Kpis.Add(new McKpiCard(
            "P(RUIN)",
            r.PRuin.ToString("P2"),
            $"of {r.Iterations:N0} simulations",
            r.PRuin > 0 ? ToneWarn : ToneGood));

        Kpis.Add(new McKpiCard(
            "P(FUNDED)",
            r.PFunded.ToString("P2"),
            "target reached",
            r.PFunded > 0 ? ToneGood : ToneDim));

        Kpis.Add(new McKpiCard(
            "MEDIAN MAX DD",
            $"{r.MaxDrawdownMedian:0.00}%",
            "peak-to-trough",
            ToneAmber));

        Kpis.Add(new McKpiCard(
            "BEST-DAY VIOL",
            r.ProbBestDayViolation.ToString("P2"),
            $">{r.BestDayRulePercent:0.#}% rule",
            r.ProbBestDayViolation > 0.05 ? ToneWarn : ToneNeutral));

        Stats.Add(new McStatSection("TERMINAL BALANCE", new[]
        {
            new McStatLine("Mean",            r.TerminalBalanceMean.ToString("N0"),   ToneNeutral),
            new McStatLine("Median",          r.TerminalBalanceMedian.ToString("N0"), ToneNeutral),
            new McStatLine("Std Dev",         r.TerminalBalanceStdDev.ToString("N0"), ToneDim),
            new McStatLine("5th percentile",  r.TerminalBalanceP05.ToString("N0"),    ToneWarn),
            new McStatLine("95th percentile", r.TerminalBalanceP95.ToString("N0"),    ToneGood),
        }));

        Stats.Add(new McStatSection("TIME TO OUTCOME", new[]
        {
            new McStatLine("Median → Fund", $"{r.MedianSlicesToFund:N0} slices",   ToneGood),
            new McStatLine("Median → Ruin", $"{r.MedianSlicesToRuin:N0} slices",   ToneWarn),
            new McStatLine("Mean → Fund",   $"{r.ExpectedSlicesToFund:N0} slices", ToneDim),
            new McStatLine("Mean → Ruin",   $"{r.ExpectedSlicesToRuin:N0} slices", ToneDim),
        }));

        Stats.Add(new McStatSection("STREAKS", new[]
        {
            new McStatLine("Median losing streak",   $"{r.MedianLongestLosingStreak:N0} trades", ToneNeutral),
            new McStatLine("95th pct losing streak", $"{r.P95LongestLosingStreak:N0} trades",    ToneWarn),
            new McStatLine("Median flat stretch",    $"{r.MedianLongestFlatStretch:N0} slices",  ToneNeutral),
            new McStatLine("95th pct flat stretch",  $"{r.P95LongestFlatStretch:N0} slices",     ToneDim),
        }));

        int ruinTotal = r.RuinByDailyFloorCount + r.RuinByMaxFloorCount;
        if (ruinTotal > 0)
        {
            double dailyPct = (double)r.RuinByDailyFloorCount / ruinTotal;
            double maxPct = (double)r.RuinByMaxFloorCount / ruinTotal;

            Stats.Add(new McStatSection("RUIN BREAKDOWN", new[]
            {
                new McStatLine("By daily floor",  $"{r.RuinByDailyFloorCount:N0}  ({dailyPct:P1})", ToneWarn),
                new McStatLine("By max-DD floor", $"{r.RuinByMaxFloorCount:N0}  ({maxPct:P1})",     ToneAccent),
            }));
        }

        CurveMemoryLabel = $"{r.Iterations:N0} sims × {r.TargetDurationSlices:N0} slices · {r.CurveMemoryBytes / (1024.0 * 1024.0):F1} MB";
    }

    private void RenderPlots(MonteCarloResult r)
    {
        if (_histPlot == null) return;
        if (r.TerminalBalances.Length == 0) return;

        var plt = _histPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_histPlot);

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(40, r.TerminalBalances);
        var barPlot = plt.Add.Bars(hist.Bins, hist.Counts);

        foreach (var bar in barPlot.Bars)
        {
            bar.FillColor = ScottPlot.Color.FromHex("#4FC3F7");
            bar.LineColor = ScottPlot.Colors.White;
            bar.LineWidth = 1;
        }

        plt.Add.VerticalLine(r.TerminalBalanceP05, 1, ScottPlot.Color.FromHex("#EF5350"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(r.TerminalBalanceMedian, 1, ScottPlot.Color.FromHex("#808080"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(r.TerminalBalanceP95, 1, ScottPlot.Color.FromHex("#26A69A"), ScottPlot.LinePattern.Solid);

        plt.Axes.AutoScale();

        plt.Title("Terminal Balance Distribution");
        plt.Axes.Bottom.Label.Text = "Terminal Balance";
        plt.Axes.Left.Label.Text = "Frequency";

        ApplyPlotTheme(_histPlot);
        _histPlot.Refresh();
    }

    private void RenderMaxDdHistogram(MonteCarloResult r)
    {
        if (_histPlot == null) return;
        if (r.MaxDrawdowns.Length == 0) return;

        var plt = _histPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_histPlot);

        double limit = _lastMaxLossPercent;
        double p99 = ComputePercentile(r.MaxDrawdowns, 99.0);
        double xMin = 0;
        double xMax = Math.Max(p99 * 1.10, limit * 1.15);

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(30, r.MaxDrawdowns);
        var barPlot = plt.Add.Bars(hist.Bins, hist.Counts);

        foreach (var bar in barPlot.Bars)
        {
            bar.FillColor = ScottPlot.Color.FromHex("#EF5350");
            bar.LineColor = ScottPlot.Colors.White;
            bar.LineWidth = 1;
        }

        int breaches = 0;
        for (int i = 0; i < r.MaxDrawdowns.Length; i++)
            if (r.MaxDrawdowns[i] >= limit) breaches++;
        double breachPct = (double)breaches / r.MaxDrawdowns.Length;

        plt.Add.VerticalLine(limit, 2, ScottPlot.Color.FromHex("#FFB74D"), ScottPlot.LinePattern.Dashed);
        plt.Add.VerticalLine(r.MaxDrawdownMedian, 1, ScottPlot.Color.FromHex("#808080"), ScottPlot.LinePattern.Solid);

        plt.Axes.AutoScale();
        plt.Axes.SetLimitsX(xMin, xMax);

        string title = breaches > 0
            ? $"Max Drawdown Distribution — {breachPct:P1} breach the {limit:0.##}% limit"
            : $"Max Drawdown Distribution — 0% breach the {limit:0.##}% limit";
        plt.Title(title);
        plt.Axes.Bottom.Label.Text = "Max Drawdown (%)";
        plt.Axes.Left.Label.Text = "Frequency";

        ApplyPlotTheme(_histPlot);
        _histPlot.Refresh();
    }

    private void RenderEquityCurves(MonteCarloResult r)
    {
        if (_curvesPlot == null) return;
        if (r.EquityCurves.Length == 0) return;
        if (r.TargetDurationSlices < 2) return;

        var data = CurrentData;
        if (data?.SourceData == null || data.SourceData.Count < 2) return;

        var plt = _curvesPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_curvesPlot);

        double t0 = data.SourceData[0].Timestamp.ToOADate();
        double t1 = data.SourceData[data.SourceData.Count - 1].Timestamp.ToOADate();
        double step = (t1 - t0) / (r.TargetDurationSlices - 1);

        var xs = new double[r.TargetDurationSlices];
        for (int k = 0; k < r.TargetDurationSlices; k++)
            xs[k] = t0 + step * k;

        var (xsDown, _) = DownsampleAxis(xs, MaxPointsPerCurve);
        double sampleStep = xs.Length > MaxPointsPerCurve
            ? (double)xs.Length / xsDown.Length
            : 1.0;

        var indices = SelectCurveIndices(r, MaxCurvesToPlot);

        var red = ScottPlot.Color.FromHex("#EF5350").WithAlpha(0.35);
        var green = ScottPlot.Color.FromHex("#26A69A").WithAlpha(0.35);
        var blue = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(0.10);

        foreach (var i in indices)
        {
            var curve = r.EquityCurves[i];
            int elapsed = r.SlicesElapsed[i];
            if (elapsed < 2) continue;

            var ysDown = DownsampleValues(curve, elapsed, xsDown.Length, sampleStep);

            var line = plt.Add.ScatterLine(xsDown, ysDown);
            line.LineWidth = 1;
            line.MarkerSize = 0;
            line.Color = r.Outcomes[i] switch
            {
                MonteCarloOutcome.Blown => red,
                MonteCarloOutcome.Funded => green,
                _ => blue
            };
        }

        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();

        plt.Title($"Equity Curves — {indices.Count} of {r.EquityCurves.Length} paths");
        plt.Axes.Bottom.Label.Text = "Date";
        plt.Axes.Left.Label.Text = "Balance";

        ApplyPlotTheme(_curvesPlot);
        _curvesPlot.Refresh();
    }

    // ── Cluster plot ────────────────────────────────────────────────

    /// <summary>
    /// Fires off the clustering on a background thread. The cluster assignment
    /// is O(N × K × D × I) which is fast, but we keep it off the UI thread
    /// anyway so a slow strategy with tens of thousands of trades does not
    /// block the toggle. Rendering happens on the UI thread in the callback.
    /// </summary>
    private void RenderClusterPlot(OverviewData data)
    {
        if (_clusterPlot == null) return;
        if (data.Trades.Count < 2) return;

        // Mark the render as pending so subsequent toggles during the async
        // window do not fire duplicate work.
        _clusterRenderedFrom = data;

        var trades = data.Trades;
        _ = Task.Run(() =>
        {
            int n = trades.Count;
            int k = Math.Max(3, Math.Min(8, (int)Math.Sqrt(n / 10.0)));
            var assignments = KMeansClusterer.Cluster(trades, k);
            Dispatcher.UIThread.Post(() => DrawClusterPlotOnUi(data, trades, assignments, k));
        });
    }

    private void DrawClusterPlotOnUi(OverviewData data, IReadOnlyList<Trade> trades, int[] assignments, int k)
    {
        if (_clusterPlot == null) return;
        if (data != CurrentData) return;

        var plt = _clusterPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_clusterPlot);

        int n = trades.Count;
        var points = new List<ClusterPoint>(n);

        // Per-cluster accumulators for the geometric centroid.
        var sumX = new double[k];
        var sumY = new double[k];
        var cnt = new int[k];

        for (int i = 0; i < n; i++)
        {
            var t = trades[i];
            double x = t.EntryTime.ToOADate();
            double y = t.PnLPercent * 100.0;
            int c = assignments[i];

            points.Add(new ClusterPoint(x, y, c, t));
            sumX[c] += x;
            sumY[c] += y;
            cnt[c]++;
        }

        // Draw each cluster as its own scatter series so the legend colors match.
        for (int c = 0; c < k; c++)
        {
            int size = cnt[c];
            if (size == 0) continue;

            var xs = new double[size];
            var ys = new double[size];
            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                if (assignments[i] != c) continue;
                xs[idx] = points[i].X;
                ys[idx] = points[i].Y;
                idx++;
            }

            var sc = plt.Add.Scatter(xs, ys);
            sc.Color = ClusterPalette[c % ClusterPalette.Length];
            sc.MarkerSize = 6;
            sc.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            sc.LineWidth = 0;
            sc.LegendText = $"Cluster {c + 1}  (n={size})";
        }

        // Centroids: large hollow diamonds on top of the scatter cloud.
        var centroids = new List<ClusterCentroid>(k);
        for (int c = 0; c < k; c++)
        {
            if (cnt[c] == 0) continue;
            double cx = sumX[c] / cnt[c];
            double cy = sumY[c] / cnt[c];
            centroids.Add(new ClusterCentroid(cx, cy, c, cnt[c]));

            var marker = plt.Add.Scatter(new[] { cx }, new[] { cy });
            marker.Color = ClusterPalette[c % ClusterPalette.Length];
            marker.MarkerSize = 22;
            marker.MarkerShape = ScottPlot.MarkerShape.OpenDiamond;
            marker.MarkerLineWidth = 2;
            marker.LineWidth = 0;
            marker.LegendText = null;
        }

        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();

        plt.Title($"Trade Clusters — {n:N0} trades, {k} clusters (click to navigate)");
        plt.Axes.Bottom.Label.Text = "Entry time";
        plt.Axes.Left.Label.Text = "PnL (%)";

        ApplyPlotTheme(_clusterPlot);
        _clusterPlot.Refresh();

        _clusterData = data;
        _clusterPoints = points;
        _clusterCentroids = centroids;
    }

    /// <summary>
    /// Called by the view's PointerPressed handler. Finds the closest centroid
    /// (within a generous pixel threshold) or, failing that, the closest trade
    /// point, and issues a NavigateToTradeMessage so the Chart tab can jump.
    /// </summary>
    public void HandleClusterPlotClick(double pixelX, double pixelY)
    {
        if (_clusterPlot == null) return;
        if (_clusterData == null || _clusterPoints == null || _clusterPoints.Count == 0) return;

        var click = new ScottPlot.Pixel((float)pixelX, (float)pixelY);
        var clickCoords = _clusterPlot.Plot.GetCoordinates(click);

        // Centroid hit: prefer a group click. Threshold is generous (40 px)
        // because the markers are large and the user's mental model is
        // "click the cluster", not "click the exact centroid pixel".
        const double centroidThresholdPx = 40.0;

        int bestCentroid = -1;
        double bestCentroidDistSq = centroidThresholdPx * centroidThresholdPx;

        if (_clusterCentroids != null)
        {
            for (int i = 0; i < _clusterCentroids.Count; i++)
            {
                var c = _clusterCentroids[i];
                var cCoords = new ScottPlot.Coordinates(c.X, c.Y);
                var cPixel = _clusterPlot.Plot.GetPixel(cCoords);
                double dx = cPixel.X - pixelX;
                double dy = cPixel.Y - pixelY;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestCentroidDistSq)
                {
                    bestCentroidDistSq = d2;
                    bestCentroid = i;
                }
            }
        }

        if (bestCentroid >= 0)
        {
            var c = _clusterCentroids![bestCentroid];
            // Pick the earliest trade in the cluster as the representative.
            Trade? first = null;
            foreach (var p in _clusterPoints)
            {
                if (p.ClusterId != c.ClusterId) continue;
                if (first == null || p.Trade.EntryTime < first.EntryTime) first = p.Trade;
            }
            if (first != null)
            {
                NavigateTo(first);
                StatusText = $"Cluster {c.ClusterId + 1} (n={c.Size}) → first trade at {first.EntryTime:yyyy-MM-dd HH:mm}";
                return;
            }
        }

        // Fallback: nearest trade point. Always accept — being able to click
        // anywhere and land on the nearest trade is more useful than forcing
        // the user to click exactly on a 6px marker.
        ClusterPoint? nearest = null;
        double nearestD2 = double.MaxValue;
        foreach (var p in _clusterPoints)
        {
            var pCoords = new ScottPlot.Coordinates(p.X, p.Y);
            var pPixel = _clusterPlot.Plot.GetPixel(pCoords);
            double dx = pPixel.X - pixelX;
            double dy = pPixel.Y - pixelY;
            double d2 = dx * dx + dy * dy;
            if (d2 < nearestD2)
            {
                nearestD2 = d2;
                nearest = p;
            }
        }

        if (nearest != null)
        {
            NavigateTo(nearest.Trade);
            StatusText = $"Trade → {nearest.Trade.Side} @ {nearest.Trade.EntryTime:yyyy-MM-dd HH:mm} · " +
                         $"PnL {nearest.Trade.PnLPercent * 100.0:+0.00;-0.00}%";
        }
    }

    private void NavigateTo(Trade trade)
    {
        if (_clusterData == null) return;

        // Fire and forget. If no receiver is registered yet (Chart tab not
        // wired), this is a no-op — nothing to guard against.
        WeakReferenceMessenger.Default.Send(new NavigateToTradeMessage(
            _clusterData.Symbol,
            _clusterData.Timeframe,
            trade.EntryTime));
    }

    private static (double[] xs, int[] sourceIndices) DownsampleAxis(double[] xs, int maxPoints)
    {
        if (xs.Length <= maxPoints)
        {
            var identity = new int[xs.Length];
            for (int i = 0; i < xs.Length; i++) identity[i] = i;
            return ((double[])xs.Clone(), identity);
        }

        int n = maxPoints;
        var outXs = new double[n];
        var srcIdx = new int[n];
        double stepD = (double)(xs.Length - 1) / (n - 1);

        for (int k = 0; k < n; k++)
        {
            int src = (int)Math.Round(k * stepD);
            if (src >= xs.Length) src = xs.Length - 1;
            outXs[k] = xs[src];
            srcIdx[k] = src;
        }

        return (outXs, srcIdx);
    }

    private static double[] DownsampleValues(double[] curve, int elapsed, int outLen, double step)
    {
        var result = new double[outLen];
        int upper = Math.Min(elapsed, curve.Length);

        for (int k = 0; k < outLen; k++)
        {
            int src = (int)Math.Round(k * step);
            if (src >= upper) src = upper - 1;
            if (src < 0) src = 0;
            result[k] = curve[src];
        }

        return result;
    }

    private static List<int> SelectCurveIndices(MonteCarloResult r, int max)
    {
        int n = r.EquityCurves.Length;
        var indices = new List<int>(Math.Min(max, n));

        for (int i = 0; i < n && indices.Count < max; i++)
        {
            if (r.Outcomes[i] != MonteCarloOutcome.Timeout)
                indices.Add(i);
        }

        if (indices.Count >= max) return indices;

        var timeouts = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (r.Outcomes[i] == MonteCarloOutcome.Timeout)
                timeouts.Add(i);
        }

        int remaining = max - indices.Count;
        if (timeouts.Count <= remaining)
        {
            indices.AddRange(timeouts);
        }
        else
        {
            double stepD = (double)timeouts.Count / remaining;
            for (int k = 0; k < remaining; k++)
                indices.Add(timeouts[(int)(k * stepD)]);
        }

        return indices;
    }

    private static double ComputePercentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        if (values.Length == 1) return values[0];

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        double rank = (p / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];

        double w = rank - lower;
        return sorted[lower] * (1 - w) + sorted[upper] * w;
    }
}