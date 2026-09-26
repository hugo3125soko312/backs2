using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
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
}

public partial class RunnerViewModel : ObservableObject
{
    private readonly StrategyCatalogService _catalog;
    private readonly StrategyManager _strat;
    private readonly DataManager _data;
    private readonly PresetService _presets;
    private readonly LastRunStore _lastRun;
    private readonly SelectionQueue _queue;

    public ObservableCollection<StrategyDescriptor> Strategies { get; } = new();
    public ObservableCollection<RunnerParameterViewModel> Parameters { get; } = new();
    public ObservableCollection<QueuedDataset> QueuedItems { get; } = new();

    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private string _sourcePreview = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private string _queueHint = "";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasParameters;
    [ObservableProperty] private bool _canRun;

    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _isProgressVisible;
    [ObservableProperty] private bool _isProgressIndeterminate;

    public event EventHandler<StrategyRunResult>? RunCompleted;

    public RunnerViewModel(
        StrategyCatalogService catalog,
        StrategyManager strat,
        DataManager data,
        PresetService presets,
        LastRunStore lastRun,
        SelectionQueue queue)
    {
        _catalog = catalog;
        _strat = strat;
        _data = data;
        _presets = presets;
        _lastRun = lastRun;
        _queue = queue;

        _catalog.Changed += OnCatalogChanged;
        _queue.Changed += OnQueueChanged;

        SyncCatalog(_catalog.Current);
        SyncQueue();
    }

    private void OnCatalogChanged(object? sender, StrategyCatalogChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => SyncCatalog(e.Strategies));
    }

    private void OnQueueChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(SyncQueue);
    }

    private void SyncCatalog(IReadOnlyList<StrategyDescriptor> latest)
    {
        var selectedPath = SelectedStrategy?.Path;

        Strategies.Clear();
        foreach (var s in latest) Strategies.Add(s);

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

    private void SyncQueue()
    {
        QueuedItems.Clear();
        foreach (var item in _queue.Snapshot())
            QueuedItems.Add(item);

        CanRun = QueuedItems.Count > 0 && SelectedStrategy != null && !IsBusy;

        QueueHint = QueuedItems.Count == 0
            ? "Queue is empty — add data in the Data tab first."
            : $"{QueuedItems.Count} dataset(s) queued.";
    }

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        if (value == null)
        {
            SourcePreview = "";
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
            SourcePreview = text;

            Parameters.Clear();
            foreach (var p in StrategyParameterParser.Parse(text))
                Parameters.Add(new RunnerParameterViewModel(p));

            HasParameters = Parameters.Count > 0;
            StatusText = $"Loaded {descriptor.Name} ({Parameters.Count} params)";
            SyncQueue();
        }
        catch (Exception ex)
        {
            SourcePreview = "";
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

        if (IsBusy) return;

        if (queued.Count > 1)
        {
            var confirmed = await ConfirmBatchAsync(queued.Count);
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

            var batchResults = new List<OverviewData>();
            int totalDatasets = queued.Count;

            for (int i = 0; i < totalDatasets; i++)
            {
                var item = queued[i];
                int datasetIndex = i;

                StatusText = $"Running {SelectedStrategy.Name} on {item.Symbol}@{item.Timeframe} ({datasetIndex + 1}/{totalDatasets})...";

                var progress = new Progress<DataProgress>(p =>
                {
                    double datasetFraction = p.IsDeterminate
                        ? p.Percent / 100.0
                        : 0.0;

                    Progress = ((datasetIndex + datasetFraction) / totalDatasets) * 100.0;

                    if (p.IsDeterminate)
                    {
                        IsProgressIndeterminate = false;
                    }
                    else if (p.Percent >= 100)
                    {
                        IsProgressIndeterminate = false;
                    }
                    else
                    {
                        IsProgressIndeterminate = true;
                    }

                    StatusText = $"[{datasetIndex + 1}/{totalDatasets}] {p.Message}";
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
            StatusText = $"Batch complete — {ok}/{total} succeeded, {totalTrades} trades total.";
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

    private async Task<bool> ConfirmBatchAsync(int count)
    {
        var owner = GetMainWindow();
        if (owner == null) return true;

        bool confirmed = false;

        var yes = new Button { Content = "Yes, continue" };
        var no = new Button { Content = "Cancel" };

        var dlg = new Window
        {
            Title = "Multiple Datasets",
            Width = 460,
            Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(16),
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"You have selected {count} datasets in the queue.",
                        FontSize = 13
                    },
                    new TextBlock
                    {
                        Text = $"Running will execute the strategy on all {count} datasets sequentially.",
                        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
                        Opacity = 0.8,
                        FontSize = 12
                    },
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
        foreach (var p in Parameters)
            values[p.Name] = p.EffectiveValue;

        _presets.Save(SelectedStrategy.Name, name, values);
        StatusText = $"Saved preset {name}";
    }

    private void ApplyPreset(Preset preset)
    {
        foreach (var p in Parameters)
        {
            if (preset.Values.TryGetValue(p.Name, out var v))
            {
                p.CurrentValue = v;
                p.CurrentBool = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }
        }
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