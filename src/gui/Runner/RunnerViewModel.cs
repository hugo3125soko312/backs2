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
    private readonly OptimizationStore _optStore;

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
        OptimizationRunner optimizer,
        OptimizationStore optStore)
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

        _catalog.Changed += OnCatalogChanged;
        _queue.Changed += OnQueueChanged;
        _accounts.ActiveChanged += OnActiveAccountChanged;

        ApplyAccountDefaults();

        Parameters.CollectionChanged += OnParametersCollectionChanged;

        SyncCatalog(_catalog.Current);
        SyncQueue();

        _ = RefreshResultsAsync();
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
}