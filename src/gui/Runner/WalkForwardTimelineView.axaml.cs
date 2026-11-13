using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using backs2.Core.WalkForward;
using ScottPlot.Avalonia;
using System;
using System.Collections.Generic;
using System.Linq;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    private const double BarHeight = 0.35;
    private AvaPlot? _plotView;
    private IReadOnlyList<WalkForwardTimelineBuilder.TimelineBar> _bars = Array.Empty<WalkForwardTimelineBuilder.TimelineBar>();
    private double[] _yPositions = Array.Empty<double>();

    public WalkForwardTimelineView()
    {
        InitializeComponent();
        _plotView = this.FindControl<AvaPlot>("TimelinePlot");
    }

    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> windowRows, IReadOnlyList<DateTime> barTimestamps)
    {
        if (_plotView == null) return;
        
        _bars = new List<WalkForwardTimelineBuilder.TimelineBar>();
        var plt = _plotView.Plot;
        plt.Clear();
        
        if (windowRows.Count == 0)
        {
            ShowEmptyState();
            return;
        }
        
        // Build timeline bars
        _bars = WalkForwardTimelineBuilder.Build(windowRows, barTimestamps).ToList();
        int count = _bars.Count;
        _yPositions = new double[count];
        
        for (int i = 0; i < count; i++)
        {
            _yPositions[i] = count - i;
        }
        
        // Draw bars
        for (int i = 0; i < count; i++)
        {
            var bar = _bars[i];
            double y = _yPositions[i];
            double yMin = y - BarHeight;
            double yMax = y + BarHeight;
            
            // Out-of-sample bar (colored by health)
            var oosBar = plt.Add.Rectangle(
                0, 1, yMin, yMax);
            oosBar.FillStyle.Color = GetHealthColor(bar.Degradation, bar.OosNetPnl);
            oosBar.LineStyle.Color = ScottPlot.Color.FromHex("#444444");
        }
        
        // Configure axes
        plt.Axes.DateTimeTicksBottom();
        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        
        AddColorThresholdLegend(plt, count);
        
        _plotView.Refresh();
    }

    public void ShowEmptyState()
    {
        ScottPlot.Plot spPlot = _plotView!.Plot;
        spPlot.Clear();
        
        spPlot.Add.Text(
            "Walk-Forward Timeline\nRun Walk-Forward, then Refresh.",
            0.5, 5)
            .LabelFontColor = ScottPlot.Color.FromHex("#666666");
        
        _plotView!.Refresh();
    }

    private ScottPlot.Color GetHealthColor(double degradation, double oosNetPnl)
    {
        string health = WalkForwardTimelineBuilder.GetHealthColor(degradation, oosNetPnl);
        return health switch
        {
            "Red" => ScottPlot.Color.FromHex("#CC3333"),
            "Amber" => ScottPlot.Color.FromHex("#CC8833"),
            _ => ScottPlot.Color.FromHex("#44AA44")
        };
    }
    
    private void AddColorThresholdLegend(ScottPlot.Plot spPlot, int count)
    {
        // Add color threshold indicators
        var goodText = spPlot.Add.Text("✓ < 5%", 0.5, count + 1);
        goodText.LabelFontColor = ScottPlot.Color.FromHex("#44AA44");
        goodText.LabelFontSize = 9;
        
        var warnText = spPlot.Add.Text("⚠ 5-15%", 0.5, count);
        warnText.LabelFontColor = ScottPlot.Color.FromHex("#CC8833");
        warnText.LabelFontSize = 9;
        
        var badText = spPlot.Add.Text("✗ > 15%", 0.5, count - 1);
        badText.LabelFontColor = ScottPlot.Color.FromHex("#CC3333");
        badText.LabelFontSize = 9;
    }

    private void ApplyPlotTheme(ScottPlot.Plot spPlot)
    {
        spPlot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        spPlot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
    }
}