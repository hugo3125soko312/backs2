using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Accounts;
using backs2.Core.Optimizer;
using backs2.Core.Overview;
using backs2.Core.Runner;

namespace backs2.ViewModels;

public partial class RunnerParameterViewModel : ObservableObject
{
    public string Name { get; }
    public string Type { get; }
    public string DefaultValue { get; }
    public IReadOnlyList<string> Options { get; }

    public bool IsBool => Type == "bool";
    public bool IsNumeric => Type == "int" || Type == "double";
    public bool IsString => Type == "string";
    public bool HasOptions => Options.Count > 0;
    public bool IsFreeText => IsString && !HasOptions;
    public bool IsBoolToggle => IsBool;
    public bool IsTextInput => !IsBool && !HasOptions;

    [ObservableProperty] private string _currentValue = "";
    [ObservableProperty] private bool _currentBool;
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _stepText = "";

    public double? MinValue     => ParseDouble(MinText);
    public double? MaxValue     => ParseDouble(MaxText);
    public double? IncrementValue => ParseDouble(StepText);

    private static double? ParseDouble(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    public bool IsDirty
    {
        get
        {
            var current = EffectiveValue;
            return !string.Equals(current, DefaultValue, StringComparison.Ordinal);
        }
    }

    public RunnerParameterViewModel(StrategyParameter p)
    {
        Name = p.Name;
        Type = p.Type;
        DefaultValue = p.DefaultValue;
        Options = p.Options;
        _currentValue = p.DefaultValue;
        _currentBool = string.Equals(p.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnCurrentBoolChanged(bool value)
    {
        CurrentValue = value ? "true" : "false";
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnCurrentValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
    }

    public string EffectiveValue => IsBool ? (CurrentBool ? "true" : "false") : CurrentValue;

    public string ValueCountText
    {
        get
        {
            int n = ValueCount;
            return n <= 1 ? "" : $"-> {n} values";
        }
    }

    public int ValueCount
    {
        get
        {
            if (!IsNumeric) return 1;

            var min = MinValue;
            var max = MaxValue;
            var step = IncrementValue;

            if (!min.HasValue || !max.HasValue || !step.HasValue) return 1;
            if (step.Value <= 0) return 1;
            if (max.Value < min.Value) return 1;

            double range = max.Value - min.Value;
            double stepVal = step.Value;

            if (string.Equals(Type, "int", StringComparison.OrdinalIgnoreCase))
                return (int)Math.Floor(range / stepVal) + 1;

            double raw = range / stepVal;
            return (int)Math.Floor(Math.Round(raw, 6)) + 1;
        }
    }

    public bool HasGridRange => ValueCount > 1;

    partial void OnMinTextChanged(string value) => NotifyGridChanged();
    partial void OnMaxTextChanged(string value) => NotifyGridChanged();
    partial void OnStepTextChanged(string value) => NotifyGridChanged();

    private void NotifyGridChanged()
    {
        OnPropertyChanged(nameof(MinValue));
        OnPropertyChanged(nameof(MaxValue));
        OnPropertyChanged(nameof(IncrementValue));
        OnPropertyChanged(nameof(ValueCount));
        OnPropertyChanged(nameof(ValueCountText));
        OnPropertyChanged(nameof(HasGridRange));
    }

    /// <summary>
    /// Serialises the grid range for persistence. Empty fields stay empty.
    /// </summary>
    public string GridRangeSerialized()
        => $"{MinText}|{MaxText}|{StepText}";

    public void ApplySerializedGridRange(string serialized)
    {
        if (string.IsNullOrEmpty(serialized)) return;

        var parts = serialized.Split('|');
        if (parts.Length >= 1) MinText = parts[0];
        if (parts.Length >= 2) MaxText = parts[1];
        if (parts.Length >= 3) StepText = parts[2];
    }
}

public partial class RunnerViewModel : ObservableObject
{
    private readonly StrategyCatalogService _catalog;
    private readonly StrategyManager _strat;
    private readonly DataManager _data;
    private readonly PresetService _presets;
    private readonly LastRunStore _lastRun;
    private readonly SelectionQueue _queue;
    private readonly AccountService _accounts;
    private readonly ConfigManager _config;
    private readonly OptimizationRunner _optimizer;

    public ObservableCollection<StrategyDescriptor> Strategies { get; } = new();
    public ObservableCollection<StrategyDescriptor> FilteredStrategies { get; } = new();
    public ObservableCollection<RunnerParameterViewModel> Parameters { get; } = new();
    public ObservableCollection<QueuedDataset> QueuedItems { get; } = new();

    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private string _strategyFilter = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _queueHint = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasParameters;
    [ObservableProperty] private bool _canRun;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _isProgressIndeterminate;

    [ObservableProperty] private bool _isMcPanelExpanded = false;
    [ObservableProperty] private int _mcIterations = 1000;
    [ObservableProperty] private int _mcBlockSize = 5;
    [ObservableProperty] private double _mcRuinThresholdPercent = 10.0;
    [ObservableProperty] private string _activeAccountName = "(none)";

    [ObservableProperty] private int _totalCombinations = 1;
    [ObservableProperty] private bool _exceedsCap;
    [ObservableProperty] private string _combinationSummary = "";
    [ObservableProperty] private bool _isOptimizing;
    [ObservableProperty] private string _etaText = "";

    private CancellationTokenSource? _optimizeCts;
    private bool _suppressGridPersist;

    public event EventHandler<StrategyRunResult>? RunCompleted;
    public event EventHandler<(string Name, string Source)>? ViewSourceRequested;

    public RunnerViewModel(
        StrategyCatalogService catalog,
        StrategyManager strat,
        DataManager data,
        PresetService presets,
        LastRunStore lastRun,
        SelectionQueue queue,
        AccountService accounts,
        ConfigManager config,
        OptimizationRunner optimizer)
    {
        _catalog = catalog;
        _strat = strat;
        _data = data;
        _presets = presets;
        _lastRun = lastRun;
        _queue = queue;
        _accounts = accounts;
        _config = config;
        _optimizer = optimizer;

        _catalog.Changed += OnCatalogChanged;
        _queue.Changed += OnQueueChanged;
        _accounts.ActiveChanged += OnActiveAccountChanged;

        ApplyAccountDefaults();

        Parameters.CollectionChanged += OnParametersCollectionChanged;

        SyncCatalog(_catalog.Current);
        SyncQueue();
    }

    private void OnActiveAccountChanged(object? sender, AccountDescriptor? descriptor)
    {
        Dispatcher.UIThread.Post(ApplyAccountDefaults);
    }

    private void ApplyAccountDefaults()
    {
        var ctx = _accounts.ResolveActiveFundedContext();
        if (ctx == null)
        {
            ActiveAccountName = _accounts.Active == null
                ? "(none)"
                : $"{_accounts.Active.Firm} - {_accounts.Active.Plan} - {_accounts.Active.Size}";
            return;
        }

        ActiveAccountName = $"{ctx.Account.Firm} - {ctx.Account.Plan} - {ctx.Account.Size}";

        if (ctx.FundedPhase.MaxLossPercent.HasValue)
            McRuinThresholdPercent = ctx.FundedPhase.MaxLossPercent.Value;
    }

    private void OnCatalogChanged(object? sender, StrategyCatalogChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => SyncCatalog(e.Strategies));
    }

    private void SyncCatalog(IReadOnlyList<StrategyDescriptor> latest)
    {
        var selectedPath = SelectedStrategy?.Path;

        Strategies.Clear();
        foreach (var s in latest) Strategies.Add(s);

        ApplyFilter();

        if (selectedPath != null)
        {
            foreach (var s in Strategies)
            {
                if (string.Equals(s.Path, selectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    SelectedStrategy = s;
                    break;
                }
            }
        }
    }

    partial void OnStrategyFilterChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var f = StrategyFilter?.Trim() ?? "";

        var wanted = new List<StrategyDescriptor>();
        foreach (var s in Strategies)
        {
            if (f.Length == 0
                || s.Name.Contains(f, StringComparison.OrdinalIgnoreCase)
                || s.Path.Contains(f, StringComparison.OrdinalIgnoreCase))
            {
                wanted.Add(s);
            }
        }

        for (int i = FilteredStrategies.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(FilteredStrategies[i]))
                FilteredStrategies.RemoveAt(i);
        }

        for (int i = 0; i < wanted.Count; i++)
        {
            var s = wanted[i];
            int curIdx = FilteredStrategies.IndexOf(s);
            if (curIdx < 0)
                FilteredStrategies.Insert(i, s);
            else if (curIdx != i)
                FilteredStrategies.Move(curIdx, i);
        }
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SyncQueue);
    }

    private void SyncQueue()
    {
        QueuedItems.Clear();
        foreach (var item in _queue.Snapshot())
            QueuedItems.Add(item);

        var distinctSymbols = QueuedItems
            .Select(q => q.Symbol)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (QueuedItems.Count == 0)
        {
            QueueHint = "Queue is empty - add data in the Data tab first.";
            CanRun = false;
        }
        else if (distinctSymbols.Count > 1)
        {
            QueueHint = $"Queue spans {distinctSymbols.Count} tickers ({string.Join(", ", distinctSymbols)}) - runner supports one ticker at a time.";
            CanRun = false;
        }
        else
        {
            var tfs = QueuedItems.Select(q => q.Timeframe).ToList();
            QueueHint = $"Running {distinctSymbols[0]} - {tfs.Count} timeframe(s): {string.Join(", ", tfs)}";
            CanRun = SelectedStrategy != null && !IsBusy;
        }
    }

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        if (value == null)
        {
            Parameters.Clear();
            HasParameters = false;
            CanRun = false;
            return;
        }

        _ = LoadStrategyAsync(value);
    }

    partial void OnIsBusyChanged(bool value) => SyncQueue();

    private async Task LoadStrategyAsync(StrategyDescriptor descriptor)
    {
        try
        {
            var text = await _catalog.ReadSourceAsync(descriptor.Path);

            _suppressGridPersist = true;
            Parameters.Clear();
            foreach (var p in StrategyParameterParser.Parse(text))
                Parameters.Add(new RunnerParameterViewModel(p));

            // Restore any previously-saved grid ranges for this strategy.
            foreach (var p in Parameters)
            {
                var key = GridKey(descriptor.Name, p.Name);
                if (_config.Settings.OptimizerGridRanges.TryGetValue(key, out var serialized))
                    p.ApplySerializedGridRange(serialized);
            }
            _suppressGridPersist = false;

            HasParameters = Parameters.Count > 0;
            RecomputeCombinations();
            StatusText = $"Loaded {descriptor.Name} ({Parameters.Count} params)";
            SyncQueue();
        }
        catch (Exception ex)
        {
            _suppressGridPersist = false;
            Parameters.Clear();
            HasParameters = false;
            StatusText = $"Failed to read {descriptor.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private void RefreshCatalog()
    {
        _catalog.Refresh();
        StatusText = $"Catalog refreshed ({Strategies.Count} strategies).";
    }

    [RelayCommand]
    private async Task ViewSourceAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        try
        {
            var source = await _catalog.ReadSourceAsync(SelectedStrategy.Path);
            ViewSourceRequested?.Invoke(this, (SelectedStrategy.Name, source));
        }
        catch (Exception ex)
        {
            StatusText = $"Failed to read source: {ex.Message}";
        }
    }

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

                var optProgress = new Progress<OptimizationProgress>(p =>
                {
                    double batchFraction = p.Fraction;
                    Progress = ((idx + batchFraction) / totalDatasets) * 100.0;

                    EtaText = p.EtaSeconds.HasValue ? FormatEta(p.EtaSeconds.Value) : "";

                    StatusText = $"[{idx + 1}/{totalDatasets}] Optimize {item.Symbol}@{item.Timeframe} - {p.Phase} {p.Completed}/{p.Total}";
                });

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

    [RelayCommand]
    private async Task OpenPresetsAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        var vm = new PresetPickerViewModel(_presets, SelectedStrategy.Name);
        var window = new Views.PresetPickerWindow(vm);

        var owner = GetMainWindow();
        if (owner == null)
        {
            StatusText = "No main window available for the preset picker.";
            return;
        }

        var preset = await window.ShowDialog<Preset?>(owner);
        if (preset == null) return;

        ApplyPreset(preset);
        StatusText = $"Applied preset {preset.PresetName}";
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        var defaultName = _presets.GetNextSequentialName(SelectedStrategy.Name);
        var name = await PromptForNameAsync(defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var values = new Dictionary<string, string>();
        var gridRanges = new Dictionary<string, string>();
        foreach (var p in Parameters)
        {
            values[p.Name] = p.EffectiveValue;
            gridRanges[p.Name] = p.GridRangeSerialized();
        }

        _presets.Save(SelectedStrategy.Name, name, values, gridRanges);
        StatusText = $"Saved preset {name}";
    }

    private void ApplyPreset(Preset preset)
    {
        _suppressGridPersist = true;

        foreach (var p in Parameters)
        {
            if (preset.Values.TryGetValue(p.Name, out var v))
            {
                p.CurrentValue = v;
                p.CurrentBool = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }

            if (preset.GridRanges != null && preset.GridRanges.TryGetValue(p.Name, out var g))
                p.ApplySerializedGridRange(g);
        }

        _suppressGridPersist = false;

        PersistAllGridRanges();
    }

    private void OnParametersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (RunnerParameterViewModel p in e.OldItems)
                p.PropertyChanged -= OnParameterPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (RunnerParameterViewModel p in e.NewItems)
                p.PropertyChanged += OnParameterPropertyChanged;
        }
        RecomputeCombinations();
    }

    private void OnParameterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        bool gridField = e.PropertyName is nameof(RunnerParameterViewModel.MinText)
                        or nameof(RunnerParameterViewModel.MaxText)
                        or nameof(RunnerParameterViewModel.StepText);

        if (gridField)
        {
            if (!_suppressGridPersist && sender is RunnerParameterViewModel p)
                PersistGridRange(p);
        }

        if (e.PropertyName is nameof(RunnerParameterViewModel.ValueCount)
            or nameof(RunnerParameterViewModel.MinText)
            or nameof(RunnerParameterViewModel.MaxText)
            or nameof(RunnerParameterViewModel.StepText))
        {
            RecomputeCombinations();
        }
    }

    private static string GridKey(string strategyName, string paramName)
        => $"{strategyName}|{paramName}";

    private void PersistGridRange(RunnerParameterViewModel p)
    {
        if (SelectedStrategy == null) return;

        var key = GridKey(SelectedStrategy.Name, p.Name);
        var serialized = p.GridRangeSerialized();

        // Treat a fully-empty range as removal so we don't accumulate empty entries.
        if (serialized == "||")
            _config.Settings.OptimizerGridRanges.Remove(key);
        else
            _config.Settings.OptimizerGridRanges[key] = serialized;

        _config.SaveConfig();
    }

    private void PersistAllGridRanges()
    {
        if (SelectedStrategy == null) return;

        foreach (var p in Parameters)
        {
            var key = GridKey(SelectedStrategy.Name, p.Name);
            var serialized = p.GridRangeSerialized();

            if (serialized == "||")
                _config.Settings.OptimizerGridRanges.Remove(key);
            else
                _config.Settings.OptimizerGridRanges[key] = serialized;
        }

        _config.SaveConfig();
    }

    private void RecomputeCombinations()
    {
        long total = 1;
        foreach (var p in Parameters)
        {
            total *= p.ValueCount;
            if (total > int.MaxValue) { total = int.MaxValue; break; }
        }

        TotalCombinations = (int)total;
        ExceedsCap = TotalCombinations > Cap;

        if (Parameters.Count == 0)
        {
            CombinationSummary = "";
        }
        else if (ExceedsCap)
        {
            CombinationSummary = $"Grid: {TotalCombinations:N0} combinations exceeds cap {Cap:N0} - will sample";
        }
        else
        {
            CombinationSummary = $"Grid: {TotalCombinations:N0} combination(s)";
        }
    }

    private int Cap => _config.Settings.OptimizerMaxCombinations > 0
        ? _config.Settings.OptimizerMaxCombinations
        : 500;

    private async Task<bool> ConfirmAsync(string title, string message, string yesLabel)
    {
        var owner = GetMainWindow();
        if (owner == null) return true;

        bool confirmed = false;

        var yes = new Button { Content = yesLabel };
        var no = new Button { Content = "Cancel" };

        var dlg = new Window
        {
            Title = title,
            Width = 500,
            Height = 220,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = Avalonia.Media.TextWrapping.Wrap, FontSize = 13 },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { no, yes }
                    }
                }
            }
        };

        yes.Click += (_, _) => { confirmed = true; dlg.Close(); };
        no.Click += (_, _) => { confirmed = false; dlg.Close(); };

        await dlg.ShowDialog(owner);
        return confirmed;
    }

    private static Window? GetMainWindow()
    {
        if (Application.Current?.ApplicationLifetime
            is IClassicDesktopStyleApplicationLifetime desk)
        {
            return desk.MainWindow;
        }
        return null;
    }

    private async Task<string?> PromptForNameAsync(string defaultName)
    {
        var owner = GetMainWindow();
        if (owner == null) return defaultName;

        var box = new TextBox { Text = defaultName, Width = 300 };
        var ok = new Button { Content = "OK" };
        var cancel = new Button { Content = "Cancel" };

        string? result = null;

        var dlg = new Window
        {
            Title = "Preset name",
            Width = 380,
            Height = 160,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Spacing = 10,
                Children =
                {
                    new TextBlock { Text = "Name for this preset:" },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Spacing = 8,
                        Children = { cancel, ok }
                    }
                }
            }
        };

        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };

        await dlg.ShowDialog(owner);
        return result;
    }
}