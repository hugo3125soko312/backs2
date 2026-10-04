using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Compute;
using backs2.Core.Overview;

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
    /// <summary>Hard cap enforced by the engine and mirrored in the UI slider.</summary>
    public static int MaxIterations => MonteCarloEngine.MaxIterations;

    private readonly LastRunStore _lastRun;
    private readonly AccountService _accounts;
    private readonly MonteCarloEngine _engine;
    private readonly ComputeWorkerManager _worker;

    private readonly Dictionary<int, MonteCarloResult> _resultsByIndex = new();
    private IReadOnlyList<OverviewData> _batch = Array.Empty<OverviewData>();

    private CancellationTokenSource? _cts;
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

    /// <summary>
    /// Resets every render-cache marker. Fields live in the Plots and
    /// Cluster partials; touching them here keeps invalidation in one
    /// place regardless of which sub-system changed.
    /// </summary>
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

    /// <summary>
    /// Rebuilds the stats panel if the selected result changed, then
    /// renders whichever plot is currently visible if its cache is stale.
    /// </summary>
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

    [RelayCommand]
    private void Cancel()
    {
        _cts?.Cancel();
        StatusText = "Cancel requested — waiting for current iteration batch to finish...";
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

        if (Iterations > MaxIterations)
        {
            StatusText = $"Iterations capped at {MaxIterations:N0} (100× default). " +
                         $"Requested {Iterations:N0} — refused.";
            return;
        }

        try
        {
            IsBusy = true;
            Progress = 0;
            ClearResults();

            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

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
                ct.ThrowIfCancellationRequested();

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

                bool curvesExpected = (long)iterations * targetDurationSlices <= MonteCarloEngine.MaxCurveCells;
                string curveNote = curvesExpected
                    ? ""
                    : " (equity curves will be dropped — memory ceiling)";

                StatusText = $"Running {data.Symbol}@{data.Timeframe} ({i + 1}/{_batch.Count}) — " +
                             $"{iterations:N0} simulations{curveNote}...";

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
                    progress: progress,
                    cancellationToken: ct), ct);

                _resultsByIndex[i] = result;

                BatchResults.Add(new McBatchRow(
                    data.Symbol,
                    data.Timeframe,
                    result.PRuin,
                    result.PFunded,
                    result.PTimeout));

                LogManager.Info(LogTag.MonteCarlo,
                    $"MC completed for {data.Symbol}@{data.Timeframe}: " +
                    $"{iterations:N0} iter, curvesRetained={result.CurvesRetained}");
            }

            HasResults = _resultsByIndex.Count > 0;

            InvalidateAll();
            RefreshForSelectedResult();

            StatusText = HasResults
                ? $"Done — Monte Carlo ran on {_resultsByIndex.Count} dataset(s)."
                : "No datasets produced a result.";
        }
        catch (OperationCanceledException)
        {
            StatusText = "Monte Carlo cancelled.";

            HasResults = _resultsByIndex.Count > 0;
            InvalidateAll();
            RefreshForSelectedResult();

            LogManager.Info(LogTag.MonteCarlo, "Monte Carlo cancelled by user");
        }
        catch (Exception ex)
        {
            StatusText = $"Monte Carlo failed: {ex.Message}";
            LogManager.Error(LogTag.MonteCarlo, "Monte Carlo failed", ex);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
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
}