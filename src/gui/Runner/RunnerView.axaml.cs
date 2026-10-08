using System;
using Avalonia.Controls;
using backs2.ViewModels;

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
        }
    }
}