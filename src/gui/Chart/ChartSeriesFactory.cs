using System;
using System.Collections.Generic;
using backs2.Core.Models;
using OxyPlot;
using OxyPlot.Axes;
using OxyPlot.Series;

namespace backs2.ViewModels;

internal readonly record struct ChartScaleState(
    bool Logarithmic,
    bool Percent,
    bool IndexedTo100,
    bool Inverted);

internal static class ChartSeriesFactory
{
    private static readonly OxyColor UpColor = OxyColor.Parse("#26A69A");
    private static readonly OxyColor DownColor = OxyColor.Parse("#EF5350");

    public static CandleStickSeries BuildCandles(
        IReadOnlyList<Kline> series,
        ChartScaleState scale)
    {
        var candles = new CandleStickSeries
        {
            YAxisKey = "Price",
            IncreasingColor = UpColor,
            DecreasingColor = DownColor,
            TrackerFormatString =
                "High: {3:0.00}\nLow: {4:0.00}\nOpen: {5:0.00}\nClose: {6:0.00}\nDate: {2:yyyy-MM-dd}",
            DataFieldX = "X",
            DataFieldHigh = "High",
            DataFieldLow = "Low",
            DataFieldOpen = "Open",
            DataFieldClose = "Close"
        };

        double baseClose = series.Count > 0 ? series[0].Close : 1.0;
        if (baseClose == 0) baseClose = 1.0;

        var items = new List<OhlcItem>(series.Count);
        for (int i = 0; i < series.Count; i++)
        {
            var k = series[i];
            double o = k.Open, h = k.High, l = k.Low, c = k.Close;

            if (scale.Percent)
            {
                o = (k.Open / baseClose - 1.0) * 100.0;
                h = (k.High / baseClose - 1.0) * 100.0;
                l = (k.Low / baseClose - 1.0) * 100.0;
                c = (k.Close / baseClose - 1.0) * 100.0;
            }
            else if (scale.IndexedTo100)
            {
                double s = 100.0 / baseClose;
                o = k.Open * s;
                h = k.High * s;
                l = k.Low * s;
                c = k.Close * s;
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

        candles.ItemsSource = items;
        return candles;
    }

    // LinearBarSeries is used in place of RectangleBarSeries because
    // RectangleBarSeries.UpdateData() throws NotImplementedException in
    // the OxyPlot.Avalonia 2.1.0 build. Per-bar coloring is achieved by
    // splitting bars into an up-series and a down-series, each with a
    // single fill color.
    public static List<Series> BuildVolume(IReadOnlyList<Kline> series)
    {
        var upSeries = new LinearBarSeries
        {
            YAxisKey = "Volume",
            FillColor = UpColor,
            StrokeColor = UpColor,
            BarWidth = 0.7,
            TrackerFormatString = "{0}\nVolume: {2:0,0}"
        };

        var downSeries = new LinearBarSeries
        {
            YAxisKey = "Volume",
            FillColor = DownColor,
            StrokeColor = DownColor,
            BarWidth = 0.7,
            TrackerFormatString = "{0}\nVolume: {2:0,0}"
        };

        var upItems = new List<VolumeBar>(series.Count);
        var downItems = new List<VolumeBar>(series.Count);

        for (int i = 0; i < series.Count; i++)
        {
            var k = series[i];
            var bar = new VolumeBar
            {
                X = DateTimeAxis.ToDouble(k.Timestamp),
                Volume = k.Volume
            };

            if (k.Close >= k.Open)
                upItems.Add(bar);
            else
                downItems.Add(bar);
        }

        upSeries.ItemsSource = upItems;
        downSeries.ItemsSource = downItems;

        return new List<Series> { upSeries, downSeries };
    }

    private sealed class OhlcItem
    {
        public double X { get; set; }
        public double Open { get; set; }
        public double High { get; set; }
        public double Low { get; set; }
        public double Close { get; set; }
    }

    private sealed class VolumeBar
    {
        public double X { get; set; }
        public double Volume { get; set; }
    }
}