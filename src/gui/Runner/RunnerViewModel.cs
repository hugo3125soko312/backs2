using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Microsoft.Win32;
using ScottPlot;
using ScottPlot.WPF;
using ScottPlot.Plottables;
using backs2.ViewModels.Messages;
using backs2.Core;
using backs2.ViewModels.Messages;
using backs2.Core.Accounts;
using backs2.Core.Optimizer;
using backs2.Core.Overview;
using backs2.Core.Runner;
using backs2.Core.WalkForward;
using backs2.Services;
using backs2.Theme;
using backs2.ViewModels.Messages;

namespace backs2.ViewModels;

public partial class RunnerViewModel : ObservableObject, INavigationTarget, IRecipient<NavigateToTradeMessage>, IRecipient<OptimizeCompletedMessage>
{
    // -- Services -----------------------------------------------------
    private readonly StrategyCatalogService _catalog;
    private readonly StrategyManager _strat;
    private readonly DataManager _data;
    private readonly PresetService _presets;
    private readonly LastRunStore _lastRun;
    private readonly SelectionQueue _queue;
    private readonly AccountService _accounts;
    private readonly ConfigManager _config;
    private readonly OptimizationRunner _optimizer;
    private readonly OptimizationStore _optStore;
    private readonly backs2.Core.Data.IQueueOriginGate _queueGate;
    private readonly WalkForwardRunner _walkForwardRunner;
    private readonly WalkForwardStore _walkForwardStore;
    private WpfPlot? _walkForwardWpfPlot;

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

    /// <summary>
    /// Which backend Monte Carlo runs on (ROADMAP Phase 29B.2). Read from and
    /// written through <see cref="ConfigManager"/>, so the choice survives a
    /// restart; the dispatcher re-reads it per dispatch, so a change applies to
    /// the next run without a restart and never splits a run in flight.
    /// </summary>
    [ObservableProperty] private string _mcBackend = "Auto";

    /// <summary>The selectable values, in display order. Bound by the ComboBox.</summary>
    public IReadOnlyList<string> McBackendOptions { get; } = AppSettings.MonteCarloBackendValues;

    [ObservableProperty] private string _activeAccountName = "(none)";
    [ObservableProperty] private int _totalCombinations = 1;
    [ObservableProperty] private bool _exceedsCap;
    [ObservableProperty] private string _combinationSummary = "";
    [ObservableProperty] private bool _isOptimizing;
    [ObservableProperty] private string _etaText = "";

    /// <summary>Which subtab is visible — driven by the shell navigation tree.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsRunnerPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsResultsPanelVisible))]
    [NotifyPropertyChangedFor(nameof(IsWalkForwardResultsPanelVisible))]
    private int _selectedSubtabIndex;

    public bool IsRunnerPanelVisible  => SelectedSubtabIndex == 0;
    public bool IsResultsPanelVisible => SelectedSubtabIndex == 1;
    public bool IsWalkForwardResultsPanelVisible => SelectedSubtabIndex == 2;

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
        OptimizationRunner optimizer,
        OptimizationStore optStore, backs2.Core.Data.IQueueOriginGate queueGate, WalkForwardRunner walkForwardRunner)
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
        _optStore = optStore;

        // Loaded through the backing field on purpose: assigning the property
        // would fire the persist handler and rewrite settings.json on every
        // startup, which is exactly the "do not write when unchanged" behaviour
        // the config layer already guarantees.
        _mcBackend = ConfigManager.NormalizeBackend(config.Settings.MonteCarloBackend);
        _wfWindowMode = ConfigManager.NormalizeWalkForwardWindowMode(config.Settings.WalkForwardWindowMode);
        _wfLengthUnit = ConfigManager.NormalizeWalkForwardLengthUnit(config.Settings.WalkForwardLengthUnit);
        _wfIsLength = config.Settings.WalkForwardIsLength;
        _wfOosLength = config.Settings.WalkForwardOosLength;
        _wfStepLength = config.Settings.WalkForwardStepLength;
        _wfMinWindows = config.Settings.WalkForwardMinWindows;
        _wfObjective = ConfigManager.NormalizeWalkForwardObjective(config.Settings.WalkForwardObjective);
        _wfCarryTopN = config.Settings.WalkForwardCarryTopN;

        _catalog.Changed += OnCatalogChanged;
        _queue.Changed += OnQueueChanged;
        _accounts.ActiveChanged += OnActiveAccountChanged;

        ApplyAccountDefaults();

        Parameters.CollectionChanged += OnParametersCollectionChanged;

        SyncCatalog(_catalog.Current);
        SyncQueue();

        _ = RefreshResultsAsync();
        _queueGate = queueGate;
        _walkForwardStore = new WalkForwardStore();
        _walkForwardRunner = walkForwardRunner;
    }

    /// <summary>Called by the shell when a Runner subtab node is selected.</summary>
    public void Navigate(int subtabIndex) => SelectedSubtabIndex = subtabIndex;

    /// <summary>
    /// Persists the backend choice. Normalizes first so a value that arrives from
    /// a binding in an unexpected case still lands in settings.json in canonical
    /// form, then writes through the same <see cref="ConfigManager"/> path the
    /// grid ranges use.
    /// </summary>
    partial void OnMcBackendChanged(string value)
    {
        var normalized = ConfigManager.NormalizeBackend(value);

        // Normalization can legitimately change the value (e.g. "gpu" -> "Gpu").
        // Writing the field back is safe: the generated setter only raises a
        // change notification when the value actually differs, so this cannot
        // recurse forever.
        if (!string.Equals(normalized, value, StringComparison.Ordinal))
            McBackend = normalized;

        _config.Settings.MonteCarloBackend = normalized;
        _config.SaveConfig();
    }

    private void OnActiveAccountChanged(object? sender, AccountDescriptor? descriptor)
    {
        System.Windows.Application.Current.Dispatcher.BeginInvoke(ApplyAccountDefaults);
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
        System.Windows.Application.Current.Dispatcher.BeginInvoke(() => SyncCatalog(e.Strategies));
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

        var wantedSet = new HashSet<StrategyDescriptor>(wanted);

        for (int i = FilteredStrategies.Count - 1; i >= 0; i--)
        {
            if (!wantedSet.Contains(FilteredStrategies[i]))
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
        System.Windows.Application.Current.Dispatcher.BeginInvoke(SyncQueue);
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
        // Implementation in another partial file
    }

    // -- Commands ------------------------------------------------------

    [RelayCommand]
    private Task RunCommand() => Run();

    [RelayCommand]
    private Task CancelCommand() => Task.CompletedTask;

    [RelayCommand]
    private Task OptimizeCommand() => RunOptimization();

    [RelayCommand]
    private Task CancelOptimizeCommand() => Task.CompletedTask;

    [RelayCommand]
    private async Task RefreshCatalogCommand() => await RefreshCatalog();

    [RelayCommand]
    private async Task RefreshResultsCommand() => await RefreshResultsAsync();

    [RelayCommand]
    private Task OpenPresetsCommand()
    {
        var win = new PresetPickerWindow();
        win.Owner = Application.Current.MainWindow;
        win.ShowDialog();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task OpenWalkForwardFolderCommand()
    {
        var folder = _walkForwardRunner.OutputDirectory;
        if (System.IO.Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task ClearWalkForwardResultsCommand()
    {
        _walkForwardStore.Clear();
        WalkForwardResultsInternal?.Clear();
        return Task.CompletedTask;
    }

    [RelayCommand]
    private Task LoadWalkForwardResultsCommand() => LoadWalkForwardResults();

    [RelayCommand]
    private Task LoadWalkForwardResults() => Task.CompletedTask;

    partial void OnParametersChanged(ObservableCollection<RunnerParameterViewModel> value);

    partial void OnIsMcPanelExpandedChanged(bool value);

    partial void OnMcIterationsChanged(int value);

    partial void OnMcBlockSizeChanged(int value);

    partial void OnMcRuinThresholdPercentChanged(double value);


    partial void OnSelectedSubtabIndexChanged(int value)
    {
        // Implementation in another partial file
    }

    partial void OnIsOptimizingChanged(bool value);

    partial void OnEtaTextChanged(string value);

    [ObservableProperty] private ObservableCollection<WalkForwardWindowResult> WalkForwardResultsInternal { get; } = new();
    [ObservableProperty] private double WalkForwardProgress;
    [ObservableProperty] private bool WalkForwardIsIndeterminate;
    [ObservableProperty] private string WalkForwardStatusText = "Ready";
    [ObservableProperty] private WalkForwardState WalkForwardState = WalkForwardState.Ready;
    [ObservableProperty] private int WalkForwardMaxProgress;
    [ObservableProperty] private WalkForwardWindowResult? SelectedWalkForwardResult;
    [ObservableProperty] private StrategyRunResult? LatestResult;
    [ObservableProperty] private string ComboSummary = "";
    [ObservableProperty] private int TotalCombinations;
    [ObservableProperty] private bool ExceedsCap;
    [ObservableProperty] private string StatusText = "Ready";

    private WpfPlot? _wfPlot;

    public WpfPlot? WfPlot
    {
        get => _wfPlot;
        set
        {
            _wfPlot = value;
            OnPropertyChanged();
        }
    }

    public void SetWfPlot(WpfPlot plot)
    {
        _wfPlot = plot;
        RenderTimeline([]);
    }

    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> windowRows, IReadOnlyList<DateTime> barTimestamps)
    {
        // Empty stub - chart logic needs to be ported from ChartView
        var plot = _wfPlot;
        if (plot == null) return;

        plot.Plot.Clear();
        plot.Plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.Plot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.Plot.Axes.Color(ScottPlot.Color.FromHex("#333333"));
        plot.Refresh();
    }

    public void ShowEmptyState()
    {
        // Empty stub
    }

    partial void OnWalkForwardStateChanged(WalkForwardState value);
    partial void OnWalkForwardProgressChanged(double value);
    partial void OnWalkForwardIsIndeterminateChanged(bool value);
    partial void OnWalkForwardStatusTextChanged(string value);
    partial void OnWalkForwardMaxProgressChanged(int value);
    partial void OnWalkForwardEtaTextChanged(string value);
    partial void OnWalkForwardWindowCountChanged(int value);
    partial void OnWalkForwardTradeCountChanged(int value);
    partial void OnHasWalkForwardResultsChanged(bool value);
    partial void OnSelectedWalkForwardResultChanged(WalkForwardWindowResult? value);
    partial void OnLatestResultChanged(StrategyRunResult? value);
    partial void OnComboSummaryChanged(string value);
    partial void OnTotalCombinationsChanged(int value);
    partial void OnExceedsCapChanged(bool value);

    partial void OnProgressChanged(double value);
    partial void OnIsProgressVisibleChanged(bool value);
    partial void OnIsProgressIndeterminateChanged(bool value);

    [RelayCommand]
    private Task OpenFolderCommand()
    {
        var folder = _config.Settings.WalkForwardOutputPath;
        if (System.IO.Directory.Exists(folder))
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(folder) { UseShellExecute = true });
        return Task.CompletedTask;
    }

    [RelayCommand]
    private void SortCombinationsCommand(string column) => SortCombinations(column);

    [RelayCommand]
    private Task SortCombinationsAsync(string column) => Task.Run(() => SortCombinations(column));

    private void SortCombinations(string column)
    {
        // TODO: Implement sort
    }

    partial void OnCanRunChanged(bool value);

    partial void OnOptimizationStateChanged(OptimizationState value);
    partial void OnOptimizationProgressChanged(double value);
    partial void OnOptimizationMaxProgressChanged(int value);
    partial void OnOptimizationStatusTextChanged(string value);
    partial void OnOptimizationEtaTextChanged(string value);
    partial void OnOptimizationCurrentPhaseChanged(int value);

    [RelayCommand]
    private Task OptimizeCommand_Executed() => RunOptimization();

    private async Task RunOptimization()
    {
        // Empty stub - actual implementation in another file
        await Task.CompletedTask;
    }

    private Task RefreshCatalog() => Task.CompletedTask;


    [RelayCommand]
    private async Task AddToQueueCommand() => await Task.CompletedTask;

    [RelayCommand]
    private async Task RemoveFromQueueCommand() => await Task.CompletedTask;

    [RelayCommand]
    private async Task ClearQueueCommand() => await Task.CompletedTask;

    [RelayCommand]
    private void AddAllToQueueCommand() { }

    [RelayCommand]
    private void StopCommand() { }

    [RelayCommand]
    private void ExportCsvCommand() { }

    [RelayCommand]
    private void ExportJsonCommand() { }

    [RelayCommand]
    private void ExportWalkForwardCsvCommand() { }

    [RelayCommand]
    private void ExportWalkForwardJsonCommand() { }

    [RelayCommand]
    private async Task LoadWalkForwardResultsCommand_Executed() => await LoadWalkForwardResults();

    [RelayCommand]
    private void FilterCommand() { }

    [RelayCommand]
    private void RunCommand_Executed() => Run();

    [RelayCommand]
    private async Task RunCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task OptimizeCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task CancelCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task FilterCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task RefreshResultsCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task ExportCsvCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task ExportJsonCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task ExportWalkForwardCsvCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task ExportWalkForwardJsonCommand_CanExecute() => await Task.Run(() => { });

    [RelayCommand]
    private async Task LoadWalkForwardResultsCommand_CanExecute() => await Task.Run(() => { });

    partial void OnWalkForwardAvaPlotChanged(AvaPlot? value);

    partial void OnLastRunChanged(StrategyRunResult? value);


    partial void OnMcPanelExpandedChanged(bool value);




    partial void OnMcBackendChanged(string value);

    partial void OnMcBackendOptionsChanged(IReadOnlyList<string> value);
}
