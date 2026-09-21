using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;

namespace backs2.ViewModels;

public partial class StratsViewModel : ObservableObject
{
    private readonly StrategyCatalogService _catalog;
    private readonly StrategyManager _strat;
    private readonly DataManager _data;

    public ObservableCollection<StrategyDescriptor> Strategies { get; } = new();

    [ObservableProperty]
    private StrategyDescriptor? _selectedStrategy;

    [ObservableProperty]
    private string _sourcePreview = "";

    [ObservableProperty]
    private string _statusText = "Ready";

    [ObservableProperty]
    private double _sharpe;

    [ObservableProperty]
    private double _winRate;

    [ObservableProperty]
    private bool _isBusy;

    public StratsViewModel(
        StrategyCatalogService catalog,
        StrategyManager strat,
        DataManager data)
    {
        _catalog = catalog;
        _strat = strat;
        _data = data;

        _catalog.Changed += OnCatalogChanged;
        SyncCatalog(_catalog.Current);
    }

    private void OnCatalogChanged(object? sender, StrategyCatalogChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() => SyncCatalog(e.Strategies));
    }

    private void SyncCatalog(IReadOnlyList<StrategyDescriptor> latest)
    {
        // Preserve selection if the same path still exists.
        var selectedPath = SelectedStrategy?.Path;

        Strategies.Clear();
        foreach (var s in latest)
            Strategies.Add(s);

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
            return;
        }

        _ = LoadSourceAsync(value);
    }

    private async Task LoadSourceAsync(StrategyDescriptor descriptor)
    {
        try
        {
            var text = await _catalog.ReadSourceAsync(descriptor.Path);
            SourcePreview = text;
            StatusText = $"Loaded {descriptor.Name}";
        }
        catch (Exception ex)
        {
            SourcePreview = "";
            StatusText = $"Failed to read {descriptor.Name}: {ex.Message}";
        }
    }

    [RelayCommand]
    private async Task RunSelectedAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "No strategy selected.";
            return;
        }

        if (IsBusy) return;

        try
        {
            IsBusy = true;

            var progress = new Progress<string>(s => StatusText = s);

            var data = await _data.GetSymbolDataAsync("BTCUSD", progress);
            if (data == null || data.Count == 0)
            {
                StatusText = "No data available.";
                return;
            }

            StatusText = $"Running {SelectedStrategy.Name}...";
            var result = await _strat.RunBacktestFromFileAsync(SelectedStrategy.Path, data);

            StatusText = result.Success
                ? $"OK  {SelectedStrategy.Name}  ({result.Duration.TotalMilliseconds:F0} ms)"
                : $"FAIL  {SelectedStrategy.Name}  {result.Error}";
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
    private void RefreshCatalog()
    {
        _catalog.Refresh();
        StatusText = $"Catalog refreshed ({Strategies.Count} strategies).";
    }
}