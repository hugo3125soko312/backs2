using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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

    public ObservableCollection<StrategyDescriptor> Strategies { get; } = new();
    public ObservableCollection<string> AvailableSymbols { get; } = new();
    public ObservableCollection<RunnerParameterViewModel> Parameters { get; } = new();

    [ObservableProperty] private StrategyDescriptor? _selectedStrategy;
    [ObservableProperty] private string _selectedSymbol = "";
    [ObservableProperty] private string _sourcePreview = "";
    [ObservableProperty] private string _statusText = "Ready";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasParameters;

    public event EventHandler<StrategyRunResult>? RunCompleted;

    public RunnerViewModel(
        StrategyCatalogService catalog,
        StrategyManager strat,
        DataManager data,
        PresetService presets,
        LastRunStore lastRun)
    {
        _catalog = catalog;
        _strat = strat;
        _data = data;
        _presets = presets;
        _lastRun = lastRun;

        _catalog.Changed += OnCatalogChanged;
        SyncCatalog(_catalog.Current);
        RefreshSymbols();
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

    partial void OnSelectedStrategyChanged(StrategyDescriptor? value)
    {
        if (value == null)
        {
            SourcePreview = "";
            Parameters.Clear();
            HasParameters = false;
            return;
        }
        _ = LoadStrategyAsync(value);
    }

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
    private void RefreshSymbols()
    {
        var previous = SelectedSymbol;

        AvailableSymbols.Clear();
        foreach (var s in _data.GetCachedSymbols())
            AvailableSymbols.Add(s);

        if (!string.IsNullOrWhiteSpace(previous)) SelectedSymbol = previous;
        else if (AvailableSymbols.Count > 0) SelectedSymbol = AvailableSymbols[0];
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
        if (string.IsNullOrWhiteSpace(SelectedSymbol))
        {
            StatusText = "No symbol selected.";
            return;
        }
        if (IsBusy) return;

        try
        {
            IsBusy = true;

            var progress = new Progress<DataProgress>(p => StatusText = p.Message);
            var data = await _data.GetSymbolDataAsync(SelectedSymbol, "1D", progress);

            if (data == null || data.Count == 0)
            {
                StatusText = $"No data available for {SelectedSymbol}.";
                return;
            }

            var paramValues = new Dictionary<string, string>();
            foreach (var p in Parameters)
                paramValues[p.Name] = p.EffectiveValue;

            StatusText = $"Running {SelectedStrategy.Name} on {SelectedSymbol}...";
            var result = await _strat.RunBacktestFromFileAsync(
                SelectedStrategy.Path,
                data,
                paramValues);

            StatusText = result.Success
                ? $"OK  {SelectedStrategy.Name}  {SelectedSymbol}  ({result.Duration.TotalMilliseconds:F0} ms, {result.Trades.Count} trades)"
                : $"FAIL  {SelectedStrategy.Name}  {result.Error}";

            _lastRun.Publish(new OverviewData(
                StrategyName: SelectedStrategy.Name,
                StrategyPath: SelectedStrategy.Path,
                Symbol: SelectedSymbol,
                Timeframe: "1D",
                RunUtc: DateTime.UtcNow,
                Duration: result.Duration,
                Trades: result.Trades,
                SourceData: data,
                Error: result.Success ? null : result.Error));

            RunCompleted?.Invoke(this, result);
            RefreshSymbols();
        }
        catch (Exception ex)
        {
            StatusText = $"Run failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
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