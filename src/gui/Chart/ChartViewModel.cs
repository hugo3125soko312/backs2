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
using OxyPlot.Axes;
using OxyPlot.Series;

namespace backs2.ViewModels;

public partial class ChartViewModel : ObservableObject
{
    private readonly DataManager _data;
    private Axis? _priceAxis;

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
        if (PlotModel == null) return;
        foreach (var axis in PlotModel.Axes)
        {
            axis.Minimum = double.NaN;
            axis.Maximum = double.NaN;
        }
        PlotModel.ResetAllAxes();
        PlotModel.InvalidatePlot(false);
    }

    [RelayCommand]
    private void ToggleLogarithmic()
    {
        IsLogarithmic = !IsLogarithmic;
        ApplyScaleType();
    }

    [RelayCommand]
    private void TogglePercent()
    {
        IsPercent = !IsPercent;
        if (IsPercent) IsIndexedTo100 = false;
        RebuildSeriesOnly();
        PlotModel?.InvalidatePlot(true);
    }

    [RelayCommand]
    private void ToggleIndexed()
    {
        IsIndexedTo100 = !IsIndexedTo100;
        if (IsIndexedTo100) IsPercent = false;
        RebuildSeriesOnly();
        PlotModel?.InvalidatePlot(true);
    }

    [RelayCommand]
    private void ToggleInvert()
    {
        IsInverted = !IsInverted;
        ApplyScaleType();
    }

    private void ApplyScaleType()
    {
        if (PlotModel == null || _priceAxis == null) return;

        bool wantLog = IsLogarithmic;
        bool isLog = _priceAxis is LogarithmicAxis;

        if (wantLog != isLog)
        {
            PlotModel.Axes.Remove(_priceAxis);
            _priceAxis = BuildPriceAxis();
            PlotModel.Axes.Add(_priceAxis);
        }

        _priceAxis.StartPosition = IsInverted ? 1 : 0;
        _priceAxis.EndPosition = IsInverted ? 0 : 1;

        RebuildSeriesOnly();
        PlotModel.InvalidatePlot(true);
    }

    private Axis BuildPriceAxis()
    {
        Axis axis;
        if (IsLogarithmic)
        {
            axis = new LogarithmicAxis
            {
                Position = AxisPosition.Right,
                TicklineColor = OxyColor.Parse("#555555"),
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                TextColor = OxyColors.White
            };
        }
        else
        {
            axis = new LinearAxis
            {
                Position = AxisPosition.Right,
                TicklineColor = OxyColor.Parse("#555555"),
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                TextColor = OxyColors.White
            };
        }
        return axis;
    }

    private void RebuildSeriesOnly()
    {
        if (PlotModel == null) return;

        for (int i = PlotModel.Series.Count - 1; i >= 0; i--)
        {
            if (PlotModel.Series[i] is CandleStickSeries)
                PlotModel.Series.RemoveAt(i);
        }

        if (Series == null || Series.Count == 0) return;

        PlotModel.Series.Add(BuildCandleSeries());
    }

    private void RebuildPlotModel()
    {
        var model = new PlotModel
        {
            Background = OxyColor.Parse("#1E1E1E"),
            PlotAreaBackground = OxyColor.Parse("#1E1E1E"),
            TextColor = OxyColors.White,
            PlotAreaBorderColor = OxyColor.Parse("#333333"),
            Padding = new OxyThickness(0)
        };

        var dateAxis = new DateTimeAxis
        {
            Position = AxisPosition.Bottom,
            StringFormat = "yyyy",
            IntervalType = DateTimeIntervalType.Years,
            MinorIntervalType = DateTimeIntervalType.Months,
            TicklineColor = OxyColor.Parse("#555555"),
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None,
            TextColor = OxyColors.White
        };
        model.Axes.Add(dateAxis);

        _priceAxis = BuildPriceAxis();
        model.Axes.Add(_priceAxis);

        if (Series != null && Series.Count > 0)
            model.Series.Add(BuildCandleSeries());

        PlotModel = model;
    }

    private CandleStickSeries BuildCandleSeries()
    {
        var series = new CandleStickSeries
        {
            IncreasingColor = OxyColors.Green,
            DecreasingColor = OxyColors.Red,
            TrackerFormatString =
                "High: {3:0.00}\nLow: {4:0.00}\nOpen: {5:0.00}\nClose: {6:0.00}\nDate: {2:yyyy-MM-dd}",
            DataFieldX = "X",
            DataFieldHigh = "High",
            DataFieldLow = "Low",
            DataFieldOpen = "Open",
            DataFieldClose = "Close"
        };

        // Determine the transform base: first close of the series.
        double baseClose = Series.Count > 0 ? Series[0].Close : 1.0;
        if (baseClose == 0) baseClose = 1.0;

        var items = new List<OhlcItem>(Series.Count);
        for (int i = 0; i < Series.Count; i++)
        {
            var k = Series[i];

            double o = k.Open, h = k.High, l = k.Low, c = k.Close;

            if (IsPercent)
            {
                // Percent change relative to the first close.
                o = (k.Open / baseClose - 1.0) * 100.0;
                h = (k.High / baseClose - 1.0) * 100.0;
                l = (k.Low / baseClose - 1.0) * 100.0;
                c = (k.Close / baseClose - 1.0) * 100.0;
            }
            else if (IsIndexedTo100)
            {
                // Rebase first close to 100.
                double scale = 100.0 / baseClose;
                o = k.Open * scale;
                h = k.High * scale;
                l = k.Low * scale;
                c = k.Close * scale;
            }

            items.Add(new OhlcItem
            {
                X = DateTimeAxis.ToDouble(k.Timestamp),
                Open = o,
                High = h,
                Low = l,
                Close = c
            });
        }
        series.ItemsSource = items;
        return series;
    }

    private sealed class OhlcItem
    {
        public double X { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
    }
}