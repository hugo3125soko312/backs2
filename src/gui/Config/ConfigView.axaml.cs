using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using backs2.ViewModels;

namespace backs2.Views;

public partial class ConfigView : UserControl
{
    public ConfigView()
    {
        InitializeComponent();
    }

    private async void OnColorButtonClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;
        if (button.Tag is not SettingItem item) return;
        if (DataContext is not ConfigViewModel vm) return;

        var owner = TopLevel.GetTopLevel(this) as Window;
        if (owner == null) return;

        var picker = new ColorPickerWindow();
        picker.Initialize(item.ColorValue);

        var ok = await picker.ShowDialog<bool>(owner);
        if (!ok) return;
        if (picker.Result is not { } color) return;

        item.ColorValue = color;

        // Apply live — the shell recolors as soon as OK is clicked.
        // Persistence still requires Save Settings.
        vm.ApplyThemePreview(item);
    }

    private void OnResetThemeClick(object? sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        vm.ResetThemeToDefaults();
    }
}