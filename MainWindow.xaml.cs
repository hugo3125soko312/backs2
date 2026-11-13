using System;
using System.Windows;
using System.Windows.Input;

namespace backs2;

public partial class MainWindow : Window
{
    private bool _everMinimized;

    public MainWindow()
    {
        InitializeComponent();
        Activated += OnActivated;
        Deactivated += OnDeactivated;
        StateChanged += OnStateChanged;
    }

    private void ApplyFullScreen()
    {
        WindowState = WindowState.Normal;
        WindowState = WindowState.Maximized;
    }

    private void OnActivated(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Normal && _everMinimized)
        {
            _everMinimized = false;
            ApplyFullScreen();
        }
    }

    private void OnDeactivated(object sender, EventArgs e)
    {
        // Not needed for basic implementation
    }

    private void OnStateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            _everMinimized = true;
        else if (WindowState == WindowState.Normal && _everMinimized)
        {
            _everMinimized = false;
            ApplyFullScreen();
        }
    }
}
