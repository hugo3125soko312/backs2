// src/gui/Chart/ChartPlotBuilder.cs
using System;
using System.Collections.Generic;
using backs2.Core.Models;
using ScottPlot;
using ScottPlot.WPF;
using ScottPlot.Plottables;

namespace backs2.ViewModels;

internal readonly record struct ChartScaleState(bool Logarithmic, bool Percent, bool IndexedTo100, bool Inverted);

internal sealed class ChartPlotBuilder
{
    private VerticalLine? _crosshair;

    public void Rebuild(WpfPlot wpfPlot, IReadOnlyList<Kline> series, ChartScaleState scale)
    {
        var plot = wpfPlot.Plot;
        plot.Clear();

        plot.FigureBackground.Color = Color.FromHex("#1E1E1E");
        plot.DataBackground.Color = Color.FromHex("#1E1E1E");
        plot.Axes.Color(Color.FromHex("#333333"));

        _crosshair = plot.Add.VerticalLine(0);
        _crosshair.IsVisible = false;
        _crosshair.Color = Color.FromHex("#888888");
        _crosshair.LineWidth = 1;

        if (series == null || series.Count == 0)
        {
            ApplyAxisLabelColor(plot);
            wpfPlot.Refresh();
            return;
        }

        // Dynamically determine the time interval for candle and bar widths
        TimeSpan interval = TimeSpan.FromDays(1);
        if (series.Count > 1)
        {
            interval = series[1].Timestamp - series[0].Timestamp;
            for (int i = 1; i < Math.Min(10, series.Count - 1); i++)
            {
                var diff = series[i + 1].Timestamp - series[i].Timestamp;
                if (diff < interval && diff.TotalMinutes > 0)
                    interval = diff;
            }
        }
        double widthDays = interval.TotalDays;

        var ohlcs = new List<OHLC>(series.Count);
        double baseClose = series[0].Close > 0 ? series[0].Close : 1;

        for (int i = 0; i < series.Count; i++)
        {
            var k = series[i];
            double o = k.Open, h = k.High, l = k.Low, c = k.Close;

            if (scale.Percent)
            {
                o = (o / baseClose - 1.0) * 100.0;
                h = (h / baseClose - 1.0) * 100.0;
                l = (l / baseClose - 1.0) * 100.0;
                c = (c / baseClose - 1.0) * 100.0;
            }
            else if (scale.IndexedTo100)
            {
                double s = 100.0 / baseClose;
                o *= s; h *= s; l *= s; c *= s;
            }

            ohlcs.Add(new OHLC(o, h, l, c, k.Timestamp, interval));
        }

        var candles = plot.Add.Candlestick(ohlcs);
        candles.Axes.YAxis = plot.Axes.Right;

        var bars = new List<ScottPlot.Bar>(series.Count);
        double maxVol = 0;

        for (int i = 0; i < series.Count; i++)
        {
            var k = series[i];
            if (k.Volume > maxVol) maxVol = k.Volume;
            bars.Add(new ScottPlot.Bar
            {
                Position = k.Timestamp.ToOADate(),
                Value = k.Volume,
                FillColor = k.Close >= k.Open ? Color.FromHex("#26A69A") : Color.FromHex("#EF5350"),
                LineWidth = 0,
                Size = widthDays * 0.8
            });
        }

        var barPlot = plot.Add.Bars(bars);
        barPlot.Axes.YAxis = plot.Axes.Left;

        // DateTimeTicksBottom REPLACES the bottom axis — must run before applying tick colors,
        // otherwise the new tick generator installs default (black) label styles.
        plot.Axes.DateTimeTicksBottom();

        plot.Axes.AutoScale();

        if (maxVol > 0)
        {
            plot.Axes.Left.Max = maxVol * 5;
            plot.Axes.Left.Min = 0;
        }

        ApplyAxisLabelColor(plot);

        wpfPlot.Refresh();
    }

    private static void ApplyAxisLabelColor(Plot plot)
    {
        var labelColor = Color.FromHex("#888888");
        plot.Axes.Bottom.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Right.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Left.TickLabelStyle.ForeColor = labelColor;
        plot.Axes.Bottom.Label.ForeColor = labelColor;
        plot.Axes.Left.Label.ForeColor = labelColor;
        plot.Axes.Right.Label.ForeColor = labelColor;
    }

    public void MoveCrosshair(WpfPlot wpfPlot, double dataX)
    {
        if (_crosshair == null) return;
        _crosshair.X = dataX;
        _crosshair.IsVisible = true;
        wpfPlot.Refresh();
    }

    public void HideCrosshair(WpfPlot wpfPlot)
    {
        if (_crosshair == null || !_crosshair.IsVisible) return;
        _crosshair.IsVisible = false;
        wpfPlot.Refresh();
    }
}
