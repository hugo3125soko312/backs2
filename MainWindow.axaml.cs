using System;
using Avalonia;
using Avalonia.Controls;
using backs2.ViewModels;

namespace backs2;

public partial class MainWindow : Window
{
    private bool _everMinimized;

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

        // Phase 36A: the perf sampler starts here rather than in
        // OnFrameworkInitializationCompleted. Starting it before the window was
        // realised crashed the process; at this point the compositor exists and
        // RequestAnimationFrame has something to attach to.
        if (DataContext is MainViewModel vm)
        {
            try { vm.StartPerf(this); }
            catch (Exception ex)
            {
                backs2.Core.LogManager.Warn(backs2.Core.LogTag.App,
                    "StartPerf threw from OnOpened: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// Taskbar restore path. Windows restore-from-minimize usually returns to
    /// the last non-minimized state (FullScreen), but the managed WindowState
    /// can transiently land on Normal. Only re-apply FullScreen after a real
    /// minimize cycle — otherwise we fight legitimate transient transitions
    /// (ScottPlot materialization, compositor churn) and flicker the window.
    /// </summary>
    private void OnActivated(object? sender, EventArgs e)
    {
        if (_everMinimized && WindowState == WindowState.Normal)
        {
            _everMinimized = false;
            ApplyFullScreen();
        }
    }

    private void OnWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != WindowStateProperty) return;

        // Only track that we were minimized. Re-applying FullScreen from here
        // is what caused the Chart-tab flicker — ScottPlot's plot control
        // transiently reports Normal during rendering, and reacting to that
        // as "user restored from minimize" rebuilt the window chrome.
        if (WindowState == WindowState.Minimized)
            _everMinimized = true;
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