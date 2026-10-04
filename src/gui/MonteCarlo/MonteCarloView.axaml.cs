using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;

namespace backs2.Views;

public partial class MonteCarloView : UserControl
{
    public MonteCarloView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            var vm = app.Services.GetRequiredService<MonteCarloViewModel>();
            DataContext = vm;
            vm.SetHistogramPlotControl(HistogramPlot);
            vm.SetCurvesPlotControl(CurvesPlot);
            vm.SetClusterPlotControl(ClusterPlot);
            ClusterPlot.PointerPressed += OnClusterPlotPointerPressed;
        }
    }

    private void OnClusterPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MonteCarloViewModel vm) return;
        var pt = e.GetPosition(ClusterPlot);
        vm.HandleClusterPlotClick(pt.X, pt.Y);
    }
}