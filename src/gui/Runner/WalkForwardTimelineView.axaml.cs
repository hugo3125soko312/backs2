using Avalonia.Controls;
using backs2.ViewModels;
using ScottPlot.Avalonia;
using System;
using System.Collections.Generic;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    private AvaPlot? _plotView;

    public WalkForwardTimelineView()
    {
        InitializeComponent();
        _plotView = this.FindControl<AvaPlot>("TimelinePlot");
    }

    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> windowRows, IReadOnlyList<DateTime> barTimestamps)
    {
        if (_plotView == null) return;
        
        var plt = _plotView.Plot;
        plt.Clear();
        
        if (windowRows.Count == 0)
        {
            plt.Add.Text("No data", 0.5, 1);
            _plotView.Refresh();
            return;
        }

        // Build timeline bars
        var bars = WalkForwardTimelineBuilder.Build(windowRows, barTimestamps);
        int count = bars.Count;
        
        // Draw bars
        for (int i = 0; i < count; i++)
        {
            var bar = bars[i];
            double y = count - i;
            
            // Out-of-sample bar (colored by health)
            plt.Add.Rectangle(
                bar.OosStart.ToOADate(),
                bar.OosEnd.ToOADate(),
                y - 0.3, y + 0.3)
                .FillStyle.Color = GetHealthColor(bar.Degradation, bar.OosNetPnl);
        }

        // Basic theme
        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        plt.Axes.DateTimeTicksBottom();
        
        _plotView.Refresh();
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
}
