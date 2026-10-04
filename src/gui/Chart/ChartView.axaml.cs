// src/gui/Chart/ChartView.axaml.cs
using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using backs2.ViewModels;
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

        _plotView = this.FindControl<AvaPlot>("Plot");

        if (_plotView != null)
        {
            _plotView.UserInputProcessor.Disable();

            _plotView.PointerMoved += OnPlotPointerMoved;
            _plotView.PointerExited += OnPlotPointerExited;

            // Tunnel-phase handler. Without this, Up/Down while the Dataset
            // or Timeframe ComboBox has focus switches the selection instead
            // of panning the chart.
            AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        }
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (_plotView == null) return;
        if (DataContext is ChartViewModel vm)
        {
            vm.SetPlotControl(_plotView);
        }
    }

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        if (_plotView == null) return;
        if (IsTextEntry(e.Source)) return;

        if (ApplyPanZoom(_plotView, e.Key))
            e.Handled = true;
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