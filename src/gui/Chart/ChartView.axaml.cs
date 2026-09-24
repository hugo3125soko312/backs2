// src\gui\Chart\ChartView.axaml.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot.Avalonia;

namespace backs2.Views;

public partial class ChartView : UserControl
{
    private AvaPlot? _plotView;

    public ChartView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<ChartViewModel>();
        }

        _plotView = this.FindControl<AvaPlot>("Plot");

        if (_plotView != null)
        {
            _plotView.PointerMoved += OnPlotPointerMoved;
            _plotView.PointerExited += OnPlotPointerExited;
            
            if (DataContext is ChartViewModel vm)
            {
                vm.SetPlotControl(_plotView);
            }
        }
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        if (_plotView == null) return;

        var pos = e.GetPosition(_plotView);
        var coords = _plotView.Plot.GetCoordinates((float)pos.X, (float)pos.Y);
        vm.UpdateCrosshair(coords.X, coords.Y);
    }

    private void OnPlotPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ChartViewModel vm) vm.ClearCrosshair();
    }
}