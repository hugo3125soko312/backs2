// src\gui\MonteCarlo\MonteCarloView.axaml.cs
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using backs2.ViewModels;
using ScottPlot.Avalonia;

namespace backs2.Views;

public partial class MonteCarloView : UserControl
{
    private const double PanFraction = 0.10;
    private const double ZoomFraction = 0.10;

    public MonteCarloView()
    {
        InitializeComponent();

        HistogramPlot.UserInputProcessor.Disable();
        CurvesPlot.UserInputProcessor.Disable();
        ClusterPlot.UserInputProcessor.Disable();

        // Tunnel-phase handler. Fires on the way down from the root
        // before any focused child (ComboBox, ToggleButton) can consume
        // arrow keys. Without this, Up/Down while the plot-mode or
        // dataset ComboBox has focus cycles the dropdown selection
        // instead of panning the plot.
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);

        ClusterPlot.PointerPressed += OnClusterPlotPointerPressed;
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is MonteCarloViewModel vm)
        {
            vm.SetHistogramPlotControl(HistogramPlot);
            vm.SetCurvesPlotControl(CurvesPlot);
            vm.SetClusterPlotControl(ClusterPlot);
        }
    }

    private void OnClusterPlotPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (DataContext is not MonteCarloViewModel vm) return;
        var pt = e.GetPosition(ClusterPlot);
        vm.HandleClusterPlotClick(pt.X, pt.Y);
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        // Text entry keeps its arrow keys. NumericUpDown's spinner and
        // in-field cursor movement both depend on them.
        if (IsTextEntry(e.Source)) return;

        var plot = VisiblePlot();
        if (plot == null) return;

        if (ApplyPanZoom(plot, e.Key))
            e.Handled = true;
    }

    private AvaPlot? VisiblePlot()
    {
        if (HistogramPlot.IsVisible) return HistogramPlot;
        if (CurvesPlot.IsVisible)    return CurvesPlot;
        if (ClusterPlot.IsVisible)   return ClusterPlot;
        return null;
    }

    private static bool IsTextEntry(object? source)
    {
        if (source is TextBox) return true;

        if (source is Visual visual)
        {
            for (var v = visual.GetVisualParent(); v != null; v = v.GetVisualParent())
            {
                if (v is NumericUpDown) return true;
                if (v is TextBox) return true;
            }
        }

        return false;
    }

    private static bool ApplyPanZoom(AvaPlot plot, Key key)
    {
        var plt = plot.Plot;
        var limits = plt.Axes.GetLimits();

        double xMin = limits.Left;
        double xMax = limits.Right;
        double yMin = limits.Bottom;
        double yMax = limits.Top;

        double xSpan = xMax - xMin;
        double ySpan = yMax - yMin;
        if (xSpan <= 0 || double.IsNaN(xSpan)) return false;
        if (ySpan <= 0 || double.IsNaN(ySpan)) return false;

        double xPan = xSpan * PanFraction;
        double yPan = ySpan * PanFraction;
        double xZoom = xSpan * ZoomFraction;

        switch (key)
        {
            case Key.Left:
                plt.Axes.SetLimitsX(xMin - xPan, xMax - xPan);
                plot.Refresh();
                return true;

            case Key.Right:
                plt.Axes.SetLimitsX(xMin + xPan, xMax + xPan);
                plot.Refresh();
                return true;

            case Key.Up:
                plt.Axes.SetLimitsY(yMin + yPan, yMax + yPan);
                plot.Refresh();
                return true;

            case Key.Down:
                plt.Axes.SetLimitsY(yMin - yPan, yMax - yPan);
                plot.Refresh();
                return true;

            case Key.OemPlus:
            case Key.Add:
                plt.Axes.SetLimitsX(xMin + xZoom, xMax - xZoom);
                plot.Refresh();
                return true;

            case Key.OemMinus:
            case Key.Subtract:
                plt.Axes.SetLimitsX(xMin - xZoom, xMax + xZoom);
                plot.Refresh();
                return true;

            default:
                return false;
        }
    }
}