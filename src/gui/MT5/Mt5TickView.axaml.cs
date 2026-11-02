// src/gui/MT5/Mt5TickView.axaml.cs
using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using backs2.ViewModels;

namespace backs2.Views;

public partial class Mt5TickView : UserControl
{
    public Mt5TickView()
    {
        InitializeComponent();
    }

    private async void OnSelectCsvClicked(object? sender, RoutedEventArgs e)
    {
        try
        {
            if (DataContext is not Mt5TickViewModel vm) return;

            var topLevel = TopLevel.GetTopLevel(this);
            if (topLevel == null) return;

            // Start in data/ticks if possible
            var startDir = await topLevel.StorageProvider.TryGetFolderFromPathAsync(
                System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "data", "ticks"));

            var files = await topLevel.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "Select MT5 Tick CSV",
                AllowMultiple = false,
                SuggestedStartLocation = startDir,
                FileTypeFilter = new[] { new FilePickerFileType("CSV Files") { Patterns = new[] { "*.csv" } } }
            });

            if (files.Count > 0)
            {
                vm.ParseFilePath = files[0].Path.LocalPath;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("[Mt5TickView.OnSelectCsvClicked] " + ex);
        }
    }
}
