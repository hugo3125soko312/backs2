using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Avalonia;
using OxyPlot.Axes;

namespace backs2.Views;

public partial class ChartView : UserControl
{
    private PlotView? _plotView;

    public ChartView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<ChartViewModel>();
        }

        _plotView = this.FindControl<PlotView>("Plot");
        if (_plotView != null)
        {
            _plotView.PointerMoved += OnPlotPointerMoved;
            _plotView.PointerExited += OnPlotPointerExited;
        }
    }

    private void OnPlotPointerMoved(object? sender, PointerEventArgs e)
    {
        if (DataContext is not ChartViewModel vm) return;
        if (_plotView?.ActualModel is not PlotModel model) return;
        if (model.Series.Count == 0) return;

        var pos = e.GetPosition(_plotView);
        var plotArea = _plotView.ActualModel?.PlotArea;
        if (plotArea == null) return;

        var area = plotArea.Value;
        if (!area.Contains(pos.X, pos.Y))
        {
            vm.ClearCrosshair();
            return;
        }

        var xAxis = model.Axes[0];
        var yAxis = FindPriceAxis(model);
        if (yAxis == null) return;

        var screenPoint = new ScreenPoint(pos.X, pos.Y);
        var dataPoint = OxyPlot.Axes.Axis.InverseTransform(screenPoint, xAxis, yAxis);

        vm.UpdateCrosshair(dataPoint.X, dataPoint.Y);
    }

    private void OnPlotPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ChartViewModel vm) vm.ClearCrosshair();
    }

    private static OxyPlot.Axes.Axis? FindPriceAxis(PlotModel model)
    {
        foreach (var axis in model.Axes)
        {
            if (axis.Key == "Price") return axis;
        }
        return null;
    }
}