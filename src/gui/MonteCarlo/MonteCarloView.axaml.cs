// src\gui\MonteCarlo\MonteCarloView.axaml.cs
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using ScottPlot.Avalonia;

namespace backs2.Views;

public partial class MonteCarloView : UserControl
{
    private const double PanFraction = 0.10;
    private const double ZoomFraction = 0.10;

    public MonteCarloView()
    {
        InitializeComponent();

        if (Application.Current is App app)
        {
            var vm = app.Services.GetRequiredService<MonteCarloViewModel>();
            DataContext = vm;

            // UserInputProcessor is on the AvaPlot control, not the Plot object.
            HistogramPlot.UserInputProcessor.Disable();
            CurvesPlot.UserInputProcessor.Disable();
            ClusterPlot.UserInputProcessor.Disable();

            vm.SetHistogramPlotControl(HistogramPlot);
            vm.SetCurvesPlotControl(CurvesPlot);
            vm.SetClusterPlotControl(ClusterPlot);

            HistogramPlot.KeyDown += OnPlotKeyDown;
            CurvesPlot.KeyDown    += OnPlotKeyDown;
            ClusterPlot.KeyDown   += OnPlotKeyDown;

            ClusterPlot.PointerPressed += OnClusterPlotPointerPressed;
        }
    }

    private void OnClusterPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MonteCarloViewModel vm) return;
        var pt = e.GetPosition(ClusterPlot);
        vm.HandleClusterPlotClick(pt.X, pt.Y);
    }

    private static void OnPlotKeyDown(object? sender, KeyEventArgs e)
    {
        if (sender is not AvaPlot plot) return;

        var plt = plot.Plot;
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
                plot.Refresh();
                e.Handled = true;
                break;

            case Key.Right:
                plt.Axes.SetLimitsX(xMin + xPan, xMax + xPan);
                plot.Refresh();
                e.Handled = true;
                break;

            case Key.Up:
                plt.Axes.SetLimitsY(yMin + yPan, yMax + yPan);
                plot.Refresh();
                e.Handled = true;
                break;

            case Key.Down:
                plt.Axes.SetLimitsY(yMin - yPan, yMax - yPan);
                plot.Refresh();
                e.Handled = true;
                break;

            case Key.OemPlus:
            case Key.Add:
                plt.Axes.SetLimitsX(xMin + xZoom, xMax - xZoom);
                plot.Refresh();
                e.Handled = true;
                break;

            case Key.OemMinus:
            case Key.Subtract:
                plt.Axes.SetLimitsX(xMin - xZoom, xMax + xZoom);
                plot.Refresh();
                e.Handled = true;
                break;
        }
    }
}