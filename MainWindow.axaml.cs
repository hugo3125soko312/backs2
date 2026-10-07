using System;
using Avalonia;
using Avalonia.Controls;

namespace backs2;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnOpened;
        Activated += OnActivated;
        PropertyChanged += OnWindowPropertyChanged;
    }

    private void OnOpened(object? sender, EventArgs e)
    {
        ApplyFullScreen();
    }

    /// <summary>
    /// Windows restore-from-minimize usually returns to the last non-minimized
    /// state (FullScreen), but the managed WindowState can transiently land on
    /// Normal and stay there. Activated fires on foreground return — the
    /// reliable point to re-apply FullScreen.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal)
            ApplyFullScreen();
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;
        if (WindowState == WindowState.Normal)
            ApplyFullScreen();
    }

    private void ApplyFullScreen()
    {
        if (WindowState == WindowState.FullScreen) return;
        WindowState = WindowState.FullScreen;
    }

    private void OnMinimizeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void OnExitClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        Close();
    }
}