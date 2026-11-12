using Avalonia.Controls;
using backs2.ViewModels;
using ScottPlot.Avalonia;
using System;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    public WalkForwardTimelineView()
    {
        InitializeComponent();
    }

    public void ShowEmptyState()
    {
        // Simple text overlay - TODO: implement actual chart
        var textBlock = SummaryText;
        textBlock.Text = "Walk-Forward Timeline\nRun Walk-Forward, then Refresh.";
        textBlock.IsVisible = true;
    }

    public static void ApplyPlotTheme(AvaPlot avaPlot)
    {
        // Theme application deferred until chart implementation
    }
}
