using Avalonia.Controls;
using backs2.ViewModels;
using ScottPlot.Avalonia;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    public WalkForwardTimelineView()
    {
        InitializeComponent();
    }

    public void ShowEmptyState()
    {
        var textBlock = SummaryText;
        if (textBlock != null)
        {
            textBlock.Text = "Walk-Forward Timeline";
            textBlock.IsVisible = true;
        }
    }

    public static void ApplyPlotTheme(AvaPlot avaPlot)
    {
        // Theme application deferred until ScottPlot API investigation
    }
}
