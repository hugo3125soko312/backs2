using System;
using Avalonia.Controls;
using Avalonia.Threading;
using backs2.ViewModels;
using backs2.Views;

namespace backs2.Views;

public partial class RunnerView : UserControl
{
    private EventHandler<(string Name, string Source)>? _viewSourceHandler;

    public RunnerView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_viewSourceHandler != null && DataContext is RunnerViewModel oldVm)
            oldVm.ViewSourceRequested -= _viewSourceHandler;

        _viewSourceHandler = null;

        if (DataContext is RunnerViewModel vm)
        {
            _viewSourceHandler = async (_, payload) =>
            {
                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner == null) return;

                var win = new StrategySourceWindow();
                win.Initialize(payload.Name, payload.Source);
                await win.ShowDialog(owner);
            };
            vm.ViewSourceRequested += _viewSourceHandler;
            
            // Hook up timeline view to render when walk-forward results change
            var timelineView = this.FindControl<WalkForwardTimelineView>("TimelineView");
            if (timelineView != null)
            {
                vm.WalkForwardWindowRows.CollectionChanged += (_, _) =>
                {
                    if (timelineView.IsVisible)
                    {
                        Dispatcher.UIThread.Invoke(() =>
                        {
                            timelineView.RenderTimeline(vm.WalkForwardWindowRows);
                        });
                    }
                };
                
                // Initial render if data already exists
                if (vm.WalkForwardWindowRows.Count > 0)
                {
                    Dispatcher.UIThread.Invoke(() =>
                    {
                        timelineView.RenderTimeline(vm.WalkForwardWindowRows);
                    });
                }
            }
        }
    }
}