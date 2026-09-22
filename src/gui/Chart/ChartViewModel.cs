using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core;
using backs2.Core.Models;
using backs2.Core.Providers;
using OxyPlot;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly DataManager _data;
    private readonly ChartPlotBuilder _builder = new();

    public ObservableCollection<string> AvailableSymbols { get; } = new();
    public ObservableCollection<string> AvailableTimeframes { get; } =
        new(TimeframeMap.All);

    [ObservableProperty] private string _selectedSymbol = "";
    [ObservableProperty] private string _selectedTimeframe = "1D";
    [ObservableProperty] private string _statusText = "Chart ready.";
    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private List<Kline> _series = new();
    [ObservableProperty] private PlotModel? _plotModel;

    [ObservableProperty] private bool _isLogarithmic;
    [ObservableProperty] private bool _isPercent;
    [ObservableProperty] private bool _isIndexedTo100;
    [ObservableProperty] private bool _isInverted;

    public ChartViewModel(DataManager data)
    {
        _data = data;
        RefreshSymbols();
        RebuildPlotModel();
    }

    [RelayCommand]
    private void RefreshSymbols()
    {
        var previous = SelectedSymbol;
        AvailableSymbols.Clear();
        foreach (var s in _data.GetCachedSymbols())
            AvailableSymbols.Add(s);

        if (!string.IsNullOrWhiteSpace(previous))
            SelectedSymbol = previous;
        else if (AvailableSymbols.Count > 0)
            SelectedSymbol = AvailableSymbols[0];
    }

    [RelayCommand]
    private async Task LoadAsync()
    {
        if (string.IsNullOrWhiteSpace(SelectedSymbol))
        {
            StatusText = "No symbol selected.";
            return;
        }

        if (IsLoading) return;

        try
        {
            IsLoading = true;

            var progress = new Progress<string>(s => StatusText = s);
            var data = await _data.GetSymbolDataAsync(SelectedSymbol, SelectedTimeframe, progress);

            if (data == null || data.Count == 0)
            {
                StatusText = $"No data for {SelectedSymbol} @ {SelectedTimeframe}.";
                Series = new List<Kline>();
                return;
            }

            Series = data;
            StatusText = $"Loaded {data.Count} bars — {SelectedSymbol} @ {SelectedTimeframe}";
        }
        catch (Exception ex)
        {
            StatusText = $"Load failed: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    partial void OnSeriesChanged(List<Kline> value)
    {
        RebuildPlotModel();
    }

    [RelayCommand]
    private void AutoScale()
    {
        _builder.AutoScale();
    }

    [RelayCommand]
    private void ToggleLogarithmic()
    {
        IsLogarithmic = !IsLogarithmic;
        ApplyScale();
    }

    [RelayCommand]
    private void TogglePercent()
    {
        IsPercent = !IsPercent;
        if (IsPercent) IsIndexedTo100 = false;
        RebuildSeries();
    }

    [RelayCommand]
    private void ToggleIndexed()
    {
        IsIndexedTo100 = !IsIndexedTo100;
        if (IsIndexedTo100) IsPercent = false;
        RebuildSeries();
    }

    [RelayCommand]
    private void ToggleInvert()
    {
        IsInverted = !IsInverted;
        ApplyScale();
    }

    private ChartScaleState ScaleState => new(
        IsLogarithmic,
        IsPercent,
        IsIndexedTo100,
        IsInverted);

    private void ApplyScale()
    {
        _builder.ApplyScale(ScaleState);
        PlotModel = _builder.Model;
    }

    private void RebuildSeries()
    {
        _builder.RebuildSeries(Series, ScaleState);
        PlotModel = _builder.Model;
    }

    private void RebuildPlotModel()
    {
        _builder.Rebuild(Series, ScaleState);
        PlotModel = _builder.Model;
    }
}