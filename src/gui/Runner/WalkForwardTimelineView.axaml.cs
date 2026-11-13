namespace backs2.Views;
using backs2.Core.WalkForward;

public partial class WalkForwardTimelineView : System.Windows.Controls.UserControl
{
    public WalkForwardTimelineView()
    {
        InitializeComponent();
    }

    // TODO: Runner walk-forward chart removed (was broken). Next step: copy and port the chart implementation from ChartView/ChartTab into Runner WF results.
    public void RenderTimeline(System.Collections.Generic.IReadOnlyList<WalkForwardWindowRow> windowRows, System.Collections.Generic.IReadOnlyList<DateTime> barTimestamps)
    {
        // Empty stub
    }

    // TODO: Runner walk-forward chart removed (was broken). Next step: copy and port the chart implementation from ChartView/ChartTab into Runner WF results.
    public void ShowEmptyState()
    {
        // Empty stub
    }
}
