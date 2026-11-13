using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using backs2.Core.WalkForward;
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
        
        AddColorThresholdLegend(plt, count);
        
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

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_plotView == null || _bars.Count == 0) return;
        
        // Get position relative to the plot
        var position = e.GetPosition(_plotView);
        
        // Simple hit test based on Y position
        int closestIndex = -1;
        double minDist = double.MaxValue;
        
        for (int i = 0; i < _yPositions.Length; i++)
        {
            double yPos = _yPositions[i];
            double dist = Math.Abs(yPos - position.Y);
            if (dist < minDist)
            {
                minDist = dist;
                closestIndex = i;
            }
        }
        
        if (closestIndex >= 0 && minDist < 20)
        {
            var bar = _bars[closestIndex];
            string details = $"W{closestIndex + 1}: OOS Net PnL: {bar.OosNetPnl:+F2}, Degradation: {bar.Degradation:F2}%";
            
            if (SummaryText != null)
            {
                SummaryText.Text = details;
                SummaryText.IsVisible = true;
            }
        }
    }
    
    private void OnPointerExited(object? sender, PointerEventArgs e)
    {
        if (SummaryText != null)
        {
            SummaryText.IsVisible = false;
        }
    }
    
    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        // Could be used for clicking on bars to select them
    }
    
    private void ApplyPlotTheme(ScottPlot.Plot spPlot)
    {
        spPlot.FigureBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
        spPlot.DataBackground.Color = ScottPlot.Color.FromHex("#1E1E1E");
    }
}