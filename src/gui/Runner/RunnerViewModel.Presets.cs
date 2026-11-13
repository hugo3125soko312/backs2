using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using backs2.Core.Runner;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    [RelayCommand]
    private async Task OpenPresetsAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        var vm = new PresetPickerViewModel(_presets, SelectedStrategy.Name);
        var window = new Views.PresetPickerWindow();

        var owner = GetMainWindow();
        if (owner == null)
        {
            StatusText = "No main window available for the preset picker.";
            return;
        }

        window.Owner = owner;
        var dialogResult = window.ShowDialog();
        if (dialogResult != true) return;
        var preset = vm.Result;

        ApplyPreset(preset);
        StatusText = $"Applied preset {preset.PresetName}";
    }

    [RelayCommand]
    private async Task SavePresetAsync()
    {
        if (SelectedStrategy == null)
        {
            StatusText = "Select a strategy first.";
            return;
        }

        var defaultName = _presets.GetNextSequentialName(SelectedStrategy.Name);
        var name = await PromptForNameAsync(defaultName);
        if (string.IsNullOrWhiteSpace(name)) return;

        var values = new Dictionary<string, string>();
        var gridRanges = new Dictionary<string, string>();
        foreach (var p in Parameters)
        {
            values[p.Name] = p.EffectiveValue;
            gridRanges[p.Name] = p.GridRangeSerialized();
        }

        _presets.Save(SelectedStrategy.Name, name, values, gridRanges);
        StatusText = $"Saved preset {name}";
    }

    private void ApplyPreset(Preset preset)
    {
        _suppressGridPersist = true;

        foreach (var p in Parameters)
        {
            if (preset.Values.TryGetValue(p.Name, out var v))
            {
                p.CurrentValue = v;
                p.CurrentBool = string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
            }

            if (preset.GridRanges != null && preset.GridRanges.TryGetValue(p.Name, out var g))
                p.ApplySerializedGridRange(g);
        }

        _suppressGridPersist = false;

        PersistAllGridRanges();
    }

    private async Task<string?> PromptForNameAsync(string defaultName)
    {
        var owner = GetMainWindow();
        if (owner == null) return defaultName;

        var box = new TextBox { Text = defaultName, Width = 300 };
        var ok = new Button { Content = "OK" };
        var cancel = new Button { Content = "Cancel" };

        string? result = null;

        var dlg = new Window
        {
            Title = "Preset name",
            Width = 380,
            Height = 160,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Content = new StackPanel
            {
                Margin = new Thickness(12),
                Children =
                {
                    new TextBlock { Text = "Name for this preset:" },
                    box,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 0, 8, 0),
                        Children = { cancel, ok }
                    }
                }
            }
        };

        ok.Click += (_, _) => { result = box.Text; dlg.Close(); };
        cancel.Click += (_, _) => { result = null; dlg.Close(); };

        dlg.Owner = owner;
        dlg.ShowDialog();
        return result;
    }
}