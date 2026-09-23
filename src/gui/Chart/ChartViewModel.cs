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

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNotLoading))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsProgressIndeterminate))]
    private bool _isProgressDeterminate;

    [ObservableProperty] private double _loadingProgress;
    [ObservableProperty] private List<Kline> _series = new();
    [ObservableProperty] private PlotModel? _plotModel;

    [ObservableProperty] private bool _isLogarithmic;
    [ObservableProperty] private bool _isPercent;
    [ObservableProperty] private bool _isIndexedTo100;
    [ObservableProperty] private bool _isInverted;

    [ObservableProperty] private bool _hasCrosshair;
    [ObservableProperty] private string _readoutTime = "";
    [ObservableProperty] private string _readoutOpen = "";
    [ObservableProperty] private string _readoutHigh = "";
    [ObservableProperty] private string _readoutLow = "";
    [ObservableProperty] private string _readoutClose = "";
    [ObservableProperty] private string _readoutVolume = "";
    [ObservableProperty] private string _readoutChange = "";

    public bool IsNotLoading => !IsLoading;
    public bool IsProgressIndeterminate => !IsProgressDeterminate;

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

    partial void OnSelectedTimeframeChanged(string value)
    {
        _ = LoadAsync();
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
            IsProgressDeterminate = false;
            LoadingProgress = 0;

            var progress = new Progress<DataProgress>(p =>
            {
                StatusText = p.Message;
                IsProgressDeterminate = p.IsDeterminate;
                LoadingProgress = p.Percent;
            });

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
        HasCrosshair = false;
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

    // Called from the view's pointer handler. Coordinates arrive in data space.
    public void UpdateCrosshair(double dataX, double dataY)
    {
        if (Series.Count == 0)
        {
            HasCrosshair = false;
            return;
        }

        int idx = NearestIndex(dataX);
        if (idx < 0)
        {
            HasCrosshair = false;
            return;
        }

        var k = Series[idx];
        double prevClose = idx > 0 ? Series[idx - 1].Close : k.Open;
        double change = k.Close - prevClose;

        ReadoutTime = k.Timestamp.ToString("yyyy-MM-dd HH:mm");
        ReadoutOpen = k.Open.ToString("0.#####");
        ReadoutHigh = k.High.ToString("0.#####");
        ReadoutLow = k.Low.ToString("0.#####");
        ReadoutClose = k.Close.ToString("0.#####");
        ReadoutVolume = k.Volume.ToString("0");
        ReadoutChange = (change >= 0 ? "+" : "") + change.ToString("0.#####");

        HasCrosshair = true;

        _builder.MoveCrosshair(dataX, dataY);
    }

    public void ClearCrosshair()
    {
        if (!HasCrosshair) return;

        HasCrosshair = false;
        _builder.HideCrosshair();
    }

    private int NearestIndex(double dataX)
    {
        if (Series.Count == 0) return -1;

        int lo = 0, hi = Series.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            double midX = OxyPlot.Axes.DateTimeAxis.ToDouble(Series[mid].Timestamp);
            if (midX < dataX) lo = mid + 1;
            else hi = mid;
        }

        double loX = OxyPlot.Axes.DateTimeAxis.ToDouble(Series[lo].Timestamp);
        if (lo > 0)
        {
            double prevX = OxyPlot.Axes.DateTimeAxis.ToDouble(Series[lo - 1].Timestamp);
            if (Math.Abs(prevX - dataX) < Math.Abs(loX - dataX)) return lo - 1;
        }
        return lo;
    }
}