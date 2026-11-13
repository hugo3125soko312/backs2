using System;
using System.Collections.Generic;
using System.Windows.Controls;
using backs2.ViewModels;
using backs2.ViewModels.Messages;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    // TODO: Implement walk-forward chart rendering with WpfPlot/ScottPlot
    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> windowRows, IReadOnlyList<DateTime> barTimestamps)
    {
        // Stub - chart rendering to be implemented
    }

    public void ShowEmptyState()
    {
        // Stub
    }
}
