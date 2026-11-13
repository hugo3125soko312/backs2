using System;
using System.Collections.Generic;
using System.Windows.Controls;
using backs2.Core.Runner;
using backs2.ViewModels;
using backs2.ViewModels.Messages;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    public void RenderTimeline(IReadOnlyList<WalkForwardWindowRow> rows, IReadOnlyList<DateTime> timestamps)
    {
        // Stub - rendering will be implemented when chart is ported
    }
}