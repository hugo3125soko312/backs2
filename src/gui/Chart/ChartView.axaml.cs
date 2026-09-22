using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class ChartView : UserControl
{
    private readonly ChartViewModel? _vm;

    public ChartView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            _vm = app.Services.GetRequiredService<ChartViewModel>();
            DataContext = _vm;
            _vm.PropertyChanged += OnVmPropertyChanged;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The ViewModel rebuilds PlotModel on Series changes.
        // No imperative UI manipulation needed — the binding handles it.
    }
}