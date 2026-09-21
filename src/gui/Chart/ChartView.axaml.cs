using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot;

namespace backs2.Views;

public partial class ChartView : UserControl
{
    private readonly ChartViewModel? _vm;

    public ChartView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            _vm = app.Services.GetRequiredService<ChartViewModel>();
            DataContext = _vm;

            // Suppress the white canvas clear on the very first render.
            PricePlot.Plot.RenderManager.ClearCanvasBeforeEachRender = false;

            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ChartViewModel.Series))
            RenderChart();
    }

    private void RenderChart()
    {
        if (_vm == null || PricePlot?.Plot == null) return;

        var plot = PricePlot.Plot;
        plot.Clear();

        // Disable the white canvas clear so the dark Border background shows through.
        plot.RenderManager.ClearCanvasBeforeEachRender = false;

        // Apply dark colors (these now apply correctly since the white clear is disabled).
        plot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plot.Grid.MajorLineColor = ScottPlot.Color.FromHex("#333333");
        plot.Grid.MinorLineColor = ScottPlot.Color.FromHex("#2A2A2A");
        plot.Axes.Color(ScottPlot.Color.FromHex("#DDDDDD"));

        var series = _vm.Series;
        if (series == null || series.Count == 0)
        {
            PricePlot.Refresh();
            return;
        }

        var ohlcs = new System.Collections.Generic.List<OHLC>(series.Count);
        for (int i = 0; i < series.Count; i++)
        {
            var k = series[i];
            ohlcs.Add(new OHLC
            {
                DateTime = k.Timestamp,
                Open = k.Open,
                High = k.High,
                Low = k.Low,
                Close = k.Close
            });
        }

        var candles = plot.Add.Candlestick(ohlcs);
        candles.Sequential = true;

        plot.Axes.DateTimeTicksBottom();

        PricePlot.Refresh();
        Dispatcher.UIThread.Post(() => PricePlot.Refresh(), DispatcherPriority.Background);
    }
}