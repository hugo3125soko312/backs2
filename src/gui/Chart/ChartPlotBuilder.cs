using System.Collections.Generic;
using backs2.Core.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace backs2.ViewModels;

// Owns the PlotModel. The ViewModel feeds it data + scale state; it owns
// every OxyPlot-specific decision (axes, series, transforms).
internal sealed class ChartPlotBuilder
{
    // TradingView-style split: ~80% price, ~20% volume, small gap between.
    private const double PriceStart = 0.22;
    private const double PriceEnd = 1.00;
    private const double VolumeStart = 0.00;
    private const double VolumeEnd = 0.18;

    private Axis? _priceAxis;

    public PlotModel Model { get; private set; } = new();

    public void Rebuild(IReadOnlyList<Kline> series, ChartScaleState scale)
    {
        var model = ChartAxisFactory.CreateEmptyModel();

        model.Axes.Add(ChartAxisFactory.CreateDateAxis());
        _priceAxis = ChartAxisFactory.CreatePriceAxis(scale, PriceStart, PriceEnd);
        model.Axes.Add(_priceAxis);
        model.Axes.Add(ChartAxisFactory.CreateVolumeAxis(VolumeStart, VolumeEnd));

        if (series is { Count: > 0 })
        {
            model.Series.Add(ChartSeriesFactory.BuildCandles(series, scale));
            foreach (var s in ChartSeriesFactory.BuildVolume(series))
                model.Series.Add(s);
        }

        Model = model;
    }

    public void RebuildSeries(IReadOnlyList<Kline> series, ChartScaleState scale)
    {
        for (int i = Model.Series.Count - 1; i >= 0; i--)
        {
            if (Model.Series[i] is CandleStickSeries || Model.Series[i] is LinearBarSeries)
                Model.Series.RemoveAt(i);
        }

        if (series is not { Count: > 0 }) return;

        Model.Series.Add(ChartSeriesFactory.BuildCandles(series, scale));
        foreach (var s in ChartSeriesFactory.BuildVolume(series))
            Model.Series.Add(s);
    }

    public void ApplyScale(ChartScaleState scale)
    {
        if (_priceAxis == null) return;

        bool wantLog = scale.Logarithmic;
        bool isLog = _priceAxis is LogarithmicAxis;

        if (wantLog != isLog)
        {
            string key = _priceAxis.Key ?? "Price";
            Model.Axes.Remove(_priceAxis);
            _priceAxis = ChartAxisFactory.CreatePriceAxis(scale, PriceStart, PriceEnd);
            Model.Axes.Add(_priceAxis);
        }
        else
        {
            _priceAxis.StartPosition = scale.Inverted ? PriceEnd : PriceStart;
            _priceAxis.EndPosition = scale.Inverted ? PriceStart : PriceEnd;
        }

        Model.InvalidatePlot(true);
    }

    public void AutoScale()
    {
        foreach (var axis in Model.Axes)
        {
            axis.Minimum = double.NaN;
            axis.Maximum = double.NaN;
        }
        Model.ResetAllAxes();
        Model.InvalidatePlot(false);
    }
}