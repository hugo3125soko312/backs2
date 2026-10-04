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
    private const double PanFraction = 0.10;
    private const double ZoomFraction = 0.10;

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
            // UserInputProcessor is on the AvaPlot control, not the Plot object.
            _plotView.UserInputProcessor.Disable();

            _plotView.PointerMoved += OnPlotPointerMoved;
            _plotView.PointerExited += OnPlotPointerExited;
            _plotView.KeyDown += OnPlotKeyDown;

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

    private void OnPlotKeyDown(object? sender, KeyEventArgs e)
    {
        if (_plotView == null) return;

        var plt = _plotView.Plot;
        var limits = plt.Axes.GetLimits();

        double xMin = limits.Left;
        double xMax = limits.Right;
        double yMin = limits.Bottom;
        double yMax = limits.Top;

        double xSpan = xMax - xMin;
        double ySpan = yMax - yMin;
        if (xSpan <= 0 || double.IsNaN(xSpan)) return;
        if (ySpan <= 0 || double.IsNaN(ySpan)) return;

        double xPan = xSpan * PanFraction;
        double yPan = ySpan * PanFraction;
        double xZoom = xSpan * ZoomFraction;

        switch (e.Key)
        {
            case Key.Left:
                plt.Axes.SetLimitsX(xMin - xPan, xMax - xPan);
                _plotView.Refresh();
                e.Handled = true;
                break;

            case Key.Right:
                plt.Axes.SetLimitsX(xMin + xPan, xMax + xPan);
                _plotView.Refresh();
                e.Handled = true;
                break;

            case Key.Up:
                // Content moves down when the viewport moves up.
                plt.Axes.SetLimitsY(yMin + yPan, yMax + yPan);
                _plotView.Refresh();
                e.Handled = true;
                break;

            case Key.Down:
                plt.Axes.SetLimitsY(yMin - yPan, yMax - yPan);
                _plotView.Refresh();
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                plt.Axes.SetLimitsX(xMin + xZoom, xMax - xZoom);
                _plotView.Refresh();
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                plt.Axes.SetLimitsX(xMin - xZoom, xMax + xZoom);
                _plotView.Refresh();
                e.Handled = true;
                break;
        }
    }
}