using System;
using Avalonia;
using Avalonia.Controls;
using backs2.Core;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class DataView : UserControl
{
    private DataViewModel? _vm;
    private EventHandler? _openQueueHandler;

    public DataView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_vm != null && _openQueueHandler != null)
        {
            _vm.OpenQueueRequested -= _openQueueHandler;
            _openQueueHandler = null;
        }

        _vm = DataContext as DataViewModel;
        if (_vm == null) return;

        if (Application.Current is App app)
        {
            _openQueueHandler = async (_, _) =>
            {
                var queueVm = new QueueViewModel(app.Services.GetRequiredService<SelectionQueue>());
                var window = new QueueWindow(queueVm);

                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner == null) return;

                await window.ShowDialog<bool>(owner);

                // QueueViewModel already committed changes on Done.
                // Force the DataViewModel to refresh queued state in case
                // the modal removed entries.
                _vm.RefreshDataCommand.Execute(null);
            };
            _vm.OpenQueueRequested += _openQueueHandler;
        }
    }
}