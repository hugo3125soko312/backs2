using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using ScottPlot.Avalonia;
using System;
using System.Collections.Generic;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    private const double BarHeight = 0.35;
    private const int MinHeightPx = 240;
    private const int PxPerWindow = 36;

    private AvaPlot? _plotView;
    private List<WalkForwardTimelineBuilder.TimelineBar> _bars = new();
    private double[] _yPositions = Array.Empty<double>();

    public WalkForwardTimelineView()
    {
        InitializeComponent();
        _plotView = this.FindControl<AvaPlot>("AvaPlotControl");
        
        // Hook up pointer events for hover
        PointerMoved += OnPointerMoved;
        PointerExited += OnPointerExited;
        PointerPressed += OnPointerPressed;
    }

    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> windowRows)
    {
        ScottPlot.Plot spPlot = _plotView!.Plot;
        spPlot.Clear();

        if (windowRows.Count == 0)
        {
            ShowEmptyState();
            return;
        }

        // Build timeline bars
        _bars = new List<WalkForwardTimelineBuilder.TimelineBar>();
        int count = windowRows.Count;
        _yPositions = new double[count];
        
        for (int i = 0; i < count; i++)
        {
            var row = windowRows[i];
            _yPositions[i] = count - i;
            
            _bars.Add(new WalkForwardTimelineBuilder.TimelineBar(
                WindowIndex: row.WindowIndex,
                IsStart: DateTime.MinValue,
                IsEnd: DateTime.MinValue,
                OosStart: DateTime.MinValue,
                OosEnd: DateTime.MinValue,
                OosNetPnl: row.OosNetPnl,
                Degradation: row.Degradation,
                Success: row.Success,
                OosTradeCount: row.OosTradeCount,
                OosMaxDrawdown: row.OosMaxDrawdown
            ));
        }

        // Draw bars
        for (int i = 0; i < count; i++)
        {
            var bar = _bars[i];
            double y = _yPositions[i];
            double yMin = y - BarHeight;
            double yMax = y + BarHeight;

            // Out-of-sample bar (colored by health)
            var oosBar = spPlot.Add.Rectangle(
                0, 1, yMin, yMax);
            oosBar.FillStyle.Color = GetHealthColor(bar.Degradation, bar.OosNetPnl);
            oosBar.LineStyle.Color = ScottPlot.Color.FromHex("#444444");

            // In-sample bar (sand color)
            var isBar = spPlot.Add.Rectangle(
                0, 0.5, yMin, yMax);
            isBar.FillStyle.Color = ScottPlot.Color.FromHex("#E0C080");
            isBar.LineStyle.Color = ScottPlot.Color.FromHex("#444444");

            // Labels
            var oosLabel = spPlot.Add.Text(
                $"W{i + 1} {WalkForwardTimelineBuilder.FormatPnl(bar.OosNetPnl)}",
                0.5, y);
            oosLabel.LabelFontColor = ScottPlot.Color.FromHex("#FFFFFF");

            var isLabel = spPlot.Add.Text("IS", 0.25, y);
            isLabel.LabelFontColor = ScottPlot.Color.FromHex("#888888");
        }

        // Configure axes
        spPlot.Axes.DateTimeTicksBottom();
        
        spPlot.Axes.Margins(left: 0.02, right: 0.02, bottom: 0.05, top: 0.05);
        spPlot.Grid.IsVisible = false;
        
        // Add legend for color thresholds
        AddColorThresholdLegend(spPlot, count);

        ApplyPlotTheme(spPlot);
        _plotView!.Refresh();

        HideSummaryText();
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

    private void HideSummaryText()
    {
        if (SummaryText != null)
        {
            SummaryText.IsVisible = false;
        }
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
