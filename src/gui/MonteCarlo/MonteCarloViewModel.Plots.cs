using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core.Models;
using backs2.ViewModels.Messages;
using ScottPlot;
using ScottPlot.WPF;
using ScottPlot.Plottables;

namespace backs2.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
public partial class MonteCarloViewModel : ObservableObject, IRecipient<NavigateToTradeMessage>
{
    private readonly MonteCarloBuilder _builder = new();
    private readonly LastRunStore _lastRunStore;

    public MonteCarloViewModel(MonteCarloBuilder builder, LastRunStore lastRunStore)
    {
        _builder = builder;
        _lastRunStore = lastRunStore;
        _lastRunStore.Changed += OnLastRunChanged;
    }

    private WpfPlot? _histPlot;
    private WpfPlot? _curvesPlot;
    private bool _isSettingsExpanded = true;

    // Set when a NavigateToTradeMessage arrives before the target series has
    // finished loading. Consumed by OnSeriesChanged after the series arrives.
    private DateTime? _pendingNavigationTime;

    [ObservableProperty] private bool _isHistogramVisible;
    [ObservableProperty] private bool _isCurvesVisible;
    [ObservableProperty] private bool _isClusterVisible;
    [ObservableProperty] private int _selectedPlotModeOptionIndex;
    [ObservableProperty] private bool _showEquityCurves = true;

    public ObservableCollection<PlotModeOption> PlotModes { get; } = new()
    {
        new("Histogram"),
        new("Equity Curves"),
        new("Cluster Plot"),
    };

    partial void OnSelectedPlotModeOptionIndexChanged(int value)
    {
        IsHistogramVisible = false;
        IsCurvesVisible = false;
        IsClusterVisible = false;

        switch (value)
        {
            case 0: IsHistogramVisible = true; break;
            case 1: IsCurvesVisible = true; break;
            case 2: IsClusterVisible = true; break;
        }
    }

    public void Receive(NavigateToTradeMessage message) => throw new NotImplementedException();

    public void SetHistogramPlotControl(WpfPlot plot)
    {
        _histPlot = plot;
        ApplyPlotTheme(plot);
    }

    public void SetCurvesPlotControl(WpfPlot plot)
    {
        _curvesPlot = plot;
        ApplyPlotTheme(plot);
    }

    partial void OnShowEquityCurvesChanged(bool value)
    {
        if (value)
        {
            SelectedPlotModeOptionIndex = 1;
        }
        else
        {
            SelectedPlotModeOptionIndex = 0;
        }
    }

    private void OnLastRunChanged(object? sender, IReadOnlyList<OverviewData> batch)
    {
        if (batch.Count == 0) return;

        var results = new List<McBatchRow>();
        var kpis = new List<McKpiCard>();
        var stats = new List<McStatSection>();

        foreach (var d in batch)
        {
            results.Add(new McBatchRow(d.Symbol, "—", "—", "—"));
        }

        if (!string.IsNullOrWhiteSpace(batch[0].Error))
        {
            stats.Add(new McStatSection("ERROR", new ObservableCollection<McStatLine>
            {
                new("Error", batch[0].Error, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(239, 83, 80))),
            }));
        }
        else
        {
            stats.Add(new McStatSection("RESULT", new ObservableCollection<McStatLine>()));
        }

        BatchResults = new ObservableCollection<McBatchRow>(results);
        Kpis = new ObservableCollection<McKpiCard>(kpis);
        Stats = new ObservableCollection<McStatSection>(stats);
        HasBatch = results.Count > 0;
        HasResults = true;

        // Redraw plots on UI thread
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            RedrawPlots(batch);
        });
    }

    private void RedrawPlots(IReadOnlyList<OverviewData> batch)
    {
        // Redraw histogram
        if (_histPlot != null && batch.Count > 0)
        {
            var histogramData = new double[batch.Count];
            for (int i = 0; i < batch.Count; i++) histogramData[i] = batch[i].NetPnl;
            _builder.DrawHistogram(_histPlot, histogramData);
        }

        // Redraw curves
        if (_curvesPlot != null && batch.Count > 0)
        {
            var curvesData = new List<CurveData>();
            foreach (var d in batch) curvesData.Add(new CurveData { Xs = new[] { 0.0, 1.0 }, Ys = new[] { d.NetPnl, d.NetPnl } });
            _builder.DrawCurves(_curvesPlot, curvesData);
        }
    }

    private static void ApplyPlotTheme(WpfPlot? plot)
    {
        if (plot == null) return;

        plot.Plot.FigureBackground.Color = Color.FromHex("#1E1E1E");
        plot.Plot.DataBackground.Color = Color.FromHex("#1E1E1E");
        plot.Plot.Axes.Color(Color.FromHex("#333333"));

        foreach (var ax in plot.Plot.Axes)
        {
            ax.TickLabelStyle.ForeColor = Color.FromHex("#888888");
            ax.Label.ForeColor = Color.FromHex("#888888");
        }
    }
}

public sealed class CurveData
{
    public double[] Xs { get; set; } = Array.Empty<double>();
    public double[] Ys { get; set; } = Array.Empty<double>();
}
