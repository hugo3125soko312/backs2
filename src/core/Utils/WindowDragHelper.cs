using System;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;

namespace backs2.Core.Utils;

/// <summary>
/// Custom-chrome "snap to monitor" gesture. Unlike Avalonia's built-in BeginMoveDrag
/// (which tracks the window live with the cursor), this helper waits for the user to
/// release the left button, then - if the release point is over a different monitor -
/// moves the window to that monitor and restores its fullscreen state.
///
/// Fails silently on non-Windows platforms or if the screen API is unavailable.
/// </summary>
public static class WindowDragHelper
{
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const double MinDragDistance = 50;

    public static void AttachTitleBarDrag(Window window, Control dragArea)
    {
        if (window == null || dragArea == null) return;

        bool armed = false;
        PixelPoint pressCursor = default;

        dragArea.AddHandler(InputElement.PointerPressedEvent, (s, e) =>
        {
            var props = e.GetCurrentPoint(window).Properties;
            if (!props.IsLeftButtonPressed) return;
            if (IsInteractive(e.Source)) return;
            armed = true;
            pressCursor = GetCursorScreenPos();
        }, RoutingStrategies.Tunnel);

        dragArea.AddHandler(InputElement.PointerReleasedEvent, (s, e) =>
        {
            if (!armed) return;
            armed = false;
            var releaseCursor = GetCursorScreenPos();
            double dx = releaseCursor.X - pressCursor.X;
            double dy = releaseCursor.Y - pressCursor.Y;
            if (Math.Abs(dx) + Math.Abs(dy) < MinDragDistance) return;

            try { SnapToScreenAt(window, releaseCursor); }
            catch { }
        }, RoutingStrategies.Tunnel);
    }

    private static PixelPoint GetCursorScreenPos()
    {
        if (GetCursorPos(out var p))
            return new PixelPoint(p.X, p.Y);
        return default;
    }

    private static void SnapToScreenAt(Window window, PixelPoint cursor)
    {
        var screens = window.Screens;
        if (screens == null) return;

        var target = screens.ScreenFromPoint(cursor);
        if (target == null) return;

        var current = screens.ScreenFromPoint(window.Position);
        if (current != null && current.Bounds == target.Bounds) return;

        if (window.WindowState != WindowState.Normal)
            window.WindowState = WindowState.Normal;

        window.Position = target.WorkingArea.TopLeft;
        window.WindowState = WindowState.FullScreen;
    }

    private static bool IsInteractive(object? source)
    {
        if (source is Button or TextBox or Menu or MenuItem or ComboBox or CheckBox) return true;
        if (source is Visual v)
        {
            foreach (var a in v.GetVisualAncestors())
            {
                if (a is Button or TextBox or Menu or MenuItem or ComboBox or CheckBox) return true;
            }
        }
        return false;
    }
}