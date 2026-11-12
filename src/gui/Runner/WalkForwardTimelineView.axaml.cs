using System;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using backs2.ViewModels;

namespace backs2.Views;

public partial class WalkForwardTimelineView : UserControl
{
    private readonly Canvas? _canvas;
    private readonly TextBlock? _tooltip;
    private readonly ItemsControl? _itemsControl;

    public WalkForwardTimelineView()
    {
        InitializeComponent();
        _canvas = this.FindControl<Canvas>("Canvas");
        _tooltip = this.FindControl<TextBlock>("Tooltip");
        _itemsControl = this.FindControl<ItemsControl>("ItemsControl");
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (DataContext is RunnerViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            if (vm.WalkForwardWindowRows is not null)
                vm.WalkForwardWindowRows.CollectionChanged += OnRowsChanged;
            RenderContent();
        }
    }

    private void OnRowsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is RunnerViewModel vm) RenderContent();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        RenderContent();
    }

    private void RenderContent()
    {
        if (DataContext is not RunnerViewModel vm || _canvas is null || _itemsControl is null) return;
        var rows = vm.WalkForwardWindowRows;
        if (rows is null || rows.Count == 0)
        {
            _itemsControl.ItemsSource = null;
            return;
        }

        _itemsControl.ItemsSource = rows;
        Canvas.SetLeft(_itemsControl, 50);
        Canvas.SetTop(_itemsControl, 20);

        // Add axis labels
        for (int i = 0; i < rows.Count; i += Math.Max(1, rows.Count / 10))
        {
            var text = new TextBlock
            {
                Text = $"W{i}",
                FontSize = 9,
                Foreground = Avalonia.Media.Brush.Parse("#E6E6E6")
            };
            Canvas.SetLeft(text, 125 + i * 130);
            Canvas.SetTop(text, 10);
            if (_canvas is not null) _canvas.Children.Add(text);
        }
    }

    private void OnMouseMove(object? sender, PointerEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_canvas is null) return;

        double relativeY = pos.Y / _canvas.Bounds.Height;
        int maxWindows = _canvas.Bounds.Height > 0 ? (int)(relativeY * 20) : 0;
        int windowIndex = Math.Clamp(maxWindows, 0, 19);

        if (DataContext is RunnerViewModel vm)
        {
            vm.SelectedWindowIndex = windowIndex;
            UpdateTooltip(windowIndex, vm);
        }
    }

    private void OnClick(object? sender, PointerPressedEventArgs e)
    {
        var pos = e.GetPosition(this);
        if (_canvas is null) return;

        double relativeY = pos.Y / _canvas.Bounds.Height;
        int maxWindows = _canvas.Bounds.Height > 0 ? (int)(relativeY * 20) : 0;
        int windowIndex = Math.Clamp(maxWindows, 0, 19);

        if (DataContext is RunnerViewModel vm)
            vm.SelectedWindowIndex = windowIndex;
    }

    private void UpdateTooltip(int windowIndex, RunnerViewModel vm)
    {
        if (_tooltip is null) return;
        var rows = vm.WalkForwardWindowRows;
        if (rows is null || windowIndex < 0 || windowIndex >= rows.Count)
        {
            _tooltip.Text = "";
            return;
        }

        var row = rows[windowIndex];
        _tooltip.Text = $"Window {windowIndex + 1}/{rows.Count} | IS [{row.IsStartBar},{row.IsEndBar}) | OOS [{row.OosStartBar},{row.OosEndBar}) | PnL: {row.OosNetPnl:+0;-0} | DD: {row.OosMaxDrawdown:F2}";
    }
}
