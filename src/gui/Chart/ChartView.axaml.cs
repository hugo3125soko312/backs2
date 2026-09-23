using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Media;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using OxyPlot;
using OxyPlot.Avalonia;
using OxyPlot.Axes;

namespace backs2.Views;

public partial class ChartView : UserControl
{
    private PlotView? _plotView;
    private Rectangle? _crosshairV;
    private Rectangle? _crosshairH;
    private TranslateTransform? _crosshairVTransform;
    private TranslateTransform? _crosshairHTransform;

    public ChartView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            DataContext = app.Services.GetRequiredService<ChartViewModel>();
        }

        _plotView = this.FindControl<PlotView>("Plot");
        _crosshairV = this.FindControl<Rectangle>("CrosshairV");
        _crosshairH = this.FindControl<Rectangle>("CrosshairH");

        _crosshairVTransform = _crosshairV?.RenderTransform as TranslateTransform;
        _crosshairHTransform = _crosshairH?.RenderTransform as TranslateTransform;

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
            HideCrosshairOverlay();
            return;
        }

        var xAxis = model.Axes[0];
        var yAxis = FindPriceAxis(model);
        if (yAxis == null) return;

        var screenPoint = new ScreenPoint(pos.X, pos.Y);
        var dataPoint = OxyPlot.Axes.Axis.InverseTransform(screenPoint, xAxis, yAxis);

        vm.UpdateCrosshair(dataPoint.X, dataPoint.Y);

        if (vm.HasCrosshair)
            SetCrosshairPosition(pos.X, pos.Y);
        else
            HideCrosshairOverlay();
    }

    private void OnPlotPointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is ChartViewModel vm) vm.ClearCrosshair();
        HideCrosshairOverlay();
    }

    private void SetCrosshairPosition(double x, double y)
    {
        if (_crosshairVTransform != null) _crosshairVTransform.X = x;
        if (_crosshairHTransform != null) _crosshairHTransform.Y = y;
    }

    private void HideCrosshairOverlay()
    {
        if (_crosshairVTransform != null) _crosshairVTransform.X = -9999;
        if (_crosshairHTransform != null) _crosshairHTransform.Y = -9999;
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