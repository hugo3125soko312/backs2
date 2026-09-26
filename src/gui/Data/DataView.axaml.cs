using System;
using Avalonia;
using Avalonia.Controls;
using backs2.Core;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class DataView : UserControl
{
    public DataView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            var vm = app.Services.GetRequiredService<DataViewModel>();
            DataContext = vm;

            vm.OpenQueueRequested += async (_, _) =>
            {
                var queueVm = new QueueViewModel(app.Services.GetRequiredService<SelectionQueue>());
                var window = new QueueWindow(queueVm);

                var owner = TopLevel.GetTopLevel(this) as Window;
                if (owner == null) return;

                await window.ShowDialog<bool>(owner);

                // QueueViewModel already committed changes on Done.
                // Force the DataViewModel to refresh queued state in case
                // the modal removed entries.
                vm.RefreshDataCommand.Execute(null);
            };
        }
    }
}