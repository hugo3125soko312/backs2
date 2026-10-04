using System;
using System.Collections.Generic;
using System.Drawing.Imaging;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using backs2.Core;
using backs2.Core.DataAutomation;
using backs2.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using AvaloniaBitmap = Avalonia.Media.Imaging.Bitmap;
using AvaloniaPoint  = Avalonia.Point;
using AvaloniaRect   = Avalonia.Rect;
using AvaloniaShapeRect = Avalonia.Controls.Shapes.Rectangle;
using DrawingPoint  = System.Drawing.Point;
using DrawingRect   = System.Drawing.Rectangle;

namespace backs2.Views;

public partial class DataAutomationView : UserControl
{
    private DataAutomationViewModel? _vm;

    public DataAutomationView()
    {
        InitializeComponent();
    }

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);

        if (DataContext is DataAutomationViewModel vm && _vm == null)
        {
            _vm = vm;
            vm.CalibrateValueRequested += async (_, key) => await RunCalibrationAsync(CalibrationMode.Region, key);
            vm.CalibrateClickRequested += async (_, _) => await RunCalibrationAsync(CalibrationMode.Point, null);
            vm.PreviewCalibrationRequested += async (_, _) => await RunPreviewAsync();
        }
    }

    private async System.Threading.Tasks.Task RunCalibrationAsync(CalibrationMode mode, string? valueKey)
    {
        if (_vm == null || Application.Current is not App app) return;

        var service = app.Services.GetRequiredService<DataAutomationService>();

        _vm.StatusText = "Focusing TradingView for calibration...";
        var snap = await service.PrepareCalibrationAsync();
        if (snap == null)
        {
            _vm.StatusText = "Failed to capture TradingView for calibration.";
            return;
        }

        AvaloniaBitmap avSnap;
        using (var ms = new MemoryStream())
        {
            snap.Image.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            avSnap = new AvaloniaBitmap(ms);
        }
        snap.Image.Dispose();

        var owner = TopLevel.GetTopLevel(this) as Window;
        int physW = snap.WindowRect.Width;
        int physH = snap.WindowRect.Height;

        double initialScale = owner?.RenderScaling ?? 1.0;
        if (initialScale <= 0) initialScale = 1.0;

        var overlay = new CalibrationOverlayWindow(avSnap, mode)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(snap.WindowRect.X, snap.WindowRect.Y),
            Width  = physW / initialScale,
            Height = physH / initialScale
        };

        void FitToMonitor()
        {
            double s = overlay.RenderScaling;
            if (s <= 0) return;
            overlay.Width  = physW / s;
            overlay.Height = physH / s;
        }

        overlay.ScalingChanged += (_, _) => FitToMonitor();
        overlay.Opened += (_, _) =>
            Dispatcher.UIThread.Post(FitToMonitor, DispatcherPriority.Loaded);

        if (owner != null) await overlay.ShowDialog(owner);
        else overlay.Show();

        double scale = overlay.RenderScaling;
        if (scale <= 0) scale = 1.0;

        if (mode == CalibrationMode.Region && valueKey != null)
        {
            var r = overlay.RegionResult;
            if (r == null || r.Value.W < 5 || r.Value.H < 5)
            {
                _vm.StatusText = $"{valueKey} calibration cancelled.";
                return;
            }

            var region = new DrawingRect(
                (int)Math.Round(r.Value.X * scale),
                (int)Math.Round(r.Value.Y * scale),
                (int)Math.Round(r.Value.W * scale),
                (int)Math.Round(r.Value.H * scale));

            _vm.ApplyValueRegion(valueKey, region);
            _vm.StatusText = $"{valueKey} calibrated: {region.Width}×{region.Height}";
        }
        else
        {
            var p = overlay.PointResult;
            if (p == null) { _vm.StatusText = "Anchor calibration cancelled."; return; }

            var point = new DrawingPoint(
                (int)Math.Round(p.Value.X * scale),
                (int)Math.Round(p.Value.Y * scale));

            _vm.ApplyClickPoint(point);
            _vm.StatusText = $"Anchor calibrated: {point.X},{point.Y}";
        }
    }

    private async System.Threading.Tasks.Task RunPreviewAsync()
    {
        if (_vm == null || Application.Current is not App app) return;

        var service = app.Services.GetRequiredService<DataAutomationService>();
        var (regions, anchor) = _vm.GetCalibrationSnapshot();

        if (regions.Count == 0 && anchor == null)
        {
            _vm.StatusText = "Nothing to preview — no calibration set.";
            return;
        }

        _vm.StatusText = "Capturing TradingView for preview...";
        var snap = await service.PrepareCalibrationAsync();
        if (snap == null)
        {
            _vm.StatusText = "Failed to capture TradingView for preview.";
            return;
        }

        AvaloniaBitmap avSnap;
        using (var ms = new MemoryStream())
        {
            snap.Image.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            avSnap = new AvaloniaBitmap(ms);
        }
        snap.Image.Dispose();

        var owner = TopLevel.GetTopLevel(this) as Window;
        int physW = snap.WindowRect.Width;
        int physH = snap.WindowRect.Height;

        double initialScale = owner?.RenderScaling ?? 1.0;
        if (initialScale <= 0) initialScale = 1.0;

        var preview = new CalibrationPreviewWindow(avSnap, regions, anchor)
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(snap.WindowRect.X, snap.WindowRect.Y),
            Width  = physW / initialScale,
            Height = physH / initialScale
        };

        void FitToMonitor()
        {
            double s = preview.RenderScaling;
            if (s <= 0) return;
            preview.Width  = physW / s;
            preview.Height = physH / s;
            preview.RefreshLayout(s);
        }

        preview.ScalingChanged += (_, _) => FitToMonitor();
        preview.Opened += (_, _) =>
            Dispatcher.UIThread.Post(FitToMonitor, DispatcherPriority.Loaded);

        if (owner != null) await preview.ShowDialog(owner);
        else preview.Show();

        _vm.StatusText = "Preview closed.";
    }
}

public sealed class CalibrationPreviewWindow : Window
{
    private readonly Canvas _canvas;
    private readonly AvaloniaBitmap _snapshot;
    private readonly Dictionary<string, DrawingRect> _regions;
    private readonly DrawingPoint? _anchor;

    public CalibrationPreviewWindow(
        AvaloniaBitmap snapshot,
        Dictionary<string, DrawingRect> regions,
        DrawingPoint? anchor)
    {
        _snapshot = snapshot;
        _regions = regions;
        _anchor = anchor;

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Hand);

        var grid = new Grid();
        grid.Children.Add(new Image { Source = snapshot, Stretch = Stretch.Fill });

        _canvas = new Canvas();
        grid.Children.Add(_canvas);

        grid.Children.Add(new TextBlock
        {
            Text = "Preview — press ESC or click anywhere to close.",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = 14
        });

        Content = grid;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
        PointerPressed += (_, _) => Close();
    }

    public void RefreshLayout(double scale)
    {
        _canvas.Children.Clear();

        foreach (var (label, rect) in _regions)
            DrawRegion(label, rect, scale);

        if (_anchor is { } a)
            DrawAnchor(a, scale);
    }

    private void DrawRegion(string label, DrawingRect rect, double scale)
    {
        double x = rect.X / scale;
        double y = rect.Y / scale;
        double w = rect.Width / scale;
        double h = rect.Height / scale;

        var box = new AvaloniaShapeRect
        {
            Width = w,
            Height = h,
            Stroke = Brushes.Lime,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x30, 0, 255, 0))
        };
        Canvas.SetLeft(box, x);
        Canvas.SetTop(box, y);
        _canvas.Children.Add(box);

        var tag = new TextBlock
        {
            Text = label,
            Foreground = Brushes.Black,
            Background = Brushes.Lime,
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(4, 1)
        };
        Canvas.SetLeft(tag, x);
        Canvas.SetTop(tag, Math.Max(0, y - 18));
        _canvas.Children.Add(tag);
    }

    private void DrawAnchor(DrawingPoint p, double scale)
    {
        double x = p.X / scale;
        double y = p.Y / scale;

        var marker = new AvaloniaShapeRect
        {
            Width = 14,
            Height = 14,
            Stroke = Brushes.Cyan,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Color.FromArgb(0x50, 0, 255, 255))
        };
        Canvas.SetLeft(marker, x - 7);
        Canvas.SetTop(marker, y - 7);
        _canvas.Children.Add(marker);

        var tag = new TextBlock
        {
            Text = "Anchor",
            Foreground = Brushes.Black,
            Background = Brushes.Cyan,
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Padding = new Thickness(4, 1)
        };
        Canvas.SetLeft(tag, x + 12);
        Canvas.SetTop(tag, y - 8);
        _canvas.Children.Add(tag);
    }
}

public sealed class CalibrationOverlayWindow : Window
{
    private readonly Canvas _root;
    private readonly AvaloniaShapeRect _dimTop, _dimBottom, _dimLeft, _dimRight, _selBorder;
    private readonly CalibrationMode _mode;

    private AvaloniaPoint _start;
    private bool _dragging;
    private AvaloniaRect _current;

    public (int X, int Y, int W, int H)? RegionResult { get; private set; }
    public (int X, int Y)? PointResult { get; private set; }

    public CalibrationOverlayWindow(AvaloniaBitmap snapshot, CalibrationMode mode)
    {
        _mode = mode;

        SystemDecorations = SystemDecorations.None;
        CanResize = false;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = new Cursor(StandardCursorType.Cross);

        var grid = new Grid();
        grid.Children.Add(new Image { Source = snapshot, Stretch = Stretch.Fill });

        _root = new Canvas();
        _dimTop    = NewDim();
        _dimBottom = NewDim();
        _dimLeft   = NewDim();
        _dimRight  = NewDim();
        _selBorder = new AvaloniaShapeRect
        {
            Stroke = Brushes.Lime,
            StrokeThickness = 2,
            Fill = Brushes.Transparent,
            IsVisible = false
        };
        _root.Children.Add(_dimTop);
        _root.Children.Add(_dimBottom);
        _root.Children.Add(_dimLeft);
        _root.Children.Add(_dimRight);
        _root.Children.Add(_selBorder);
        grid.Children.Add(_root);

        grid.Children.Add(new TextBlock
        {
            Text = mode == CalibrationMode.Region
                ? "Drag over the value (digits only).  ESC to cancel."
                : "Click empty space to the right of the newest candle.  ESC to cancel.",
            Foreground = Brushes.White,
            Background = new SolidColorBrush(Color.FromArgb(0xA0, 0, 0, 0)),
            Padding = new Thickness(12, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 16, 0, 0),
            FontSize = 14
        });

        Content = grid;

        PointerPressed  += OnPointerPressed;
        PointerMoved    += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        KeyDown         += OnKeyDown;
    }

    private static AvaloniaShapeRect NewDim() =>
        new() { Fill = new SolidColorBrush(Color.FromArgb(0x80, 0, 0, 0)) };

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var p = e.GetPosition(this);

        if (_mode == CalibrationMode.Point)
        {
            PointResult = ((int)Math.Round(p.X), (int)Math.Round(p.Y));
            Close();
            e.Handled = true;
            return;
        }

        _start = p;
        _dragging = true;
        UpdateSelection(new AvaloniaRect(_start, new Size(0, 0)));
        e.Handled = true;
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_mode == CalibrationMode.Point || !_dragging) return;

        var p = e.GetPosition(this);
        var rect = new AvaloniaRect(
            Math.Min(_start.X, p.X),
            Math.Min(_start.Y, p.Y),
            Math.Abs(p.X - _start.X),
            Math.Abs(p.Y - _start.Y));
        UpdateSelection(rect);
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_mode == CalibrationMode.Point || !_dragging) return;
        _dragging = false;

        if (_current.Width >= 5 && _current.Height >= 5)
            RegionResult = ((int)Math.Round(_current.X), (int)Math.Round(_current.Y),
                            (int)Math.Round(_current.Width), (int)Math.Round(_current.Height));

        Close();
        e.Handled = true;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            RegionResult = null;
            PointResult = null;
            Close();
        }
    }

    private void UpdateSelection(AvaloniaRect rect)
    {
        _current = rect;

        double w = Bounds.Width;
        double h = Bounds.Height;
        if (w <= 0 || h <= 0) return;

        SetStrip(_dimTop, 0, 0, w, rect.Top);
        SetStrip(_dimBottom, 0, rect.Bottom, w, Math.Max(0, h - rect.Bottom));
        SetStrip(_dimLeft, 0, rect.Top, rect.Left, rect.Height);
        SetStrip(_dimRight, rect.Right, rect.Top, Math.Max(0, w - rect.Right), rect.Height);

        _selBorder.Width = rect.Width;
        _selBorder.Height = rect.Height;
        Canvas.SetLeft(_selBorder, rect.Left);
        Canvas.SetTop(_selBorder, rect.Top);
        _selBorder.IsVisible = rect.Width > 0 && rect.Height > 0;
    }

    private static void SetStrip(AvaloniaShapeRect r, double x, double y, double w, double h)
    {
        r.Width = Math.Max(0, w);
        r.Height = Math.Max(0, h);
        Canvas.SetLeft(r, x);
        Canvas.SetTop(r, y);
    }
}