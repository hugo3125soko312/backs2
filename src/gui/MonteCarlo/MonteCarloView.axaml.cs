using Avalonia;
using Avalonia.Controls;
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
            vm.SetPlotControl(PlotControl);
        }
    }
}