using System;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using backs2.Core.Runner;

namespace backs2.ViewModels;

public partial class PresetPickerViewModel : ObservableObject
{
    private readonly PresetService _presets;
    private readonly string _strategyName;

    public ObservableCollection<PresetDescriptor> Presets { get; } = new();

    [ObservableProperty] private bool _showAll;
    [ObservableProperty] private PresetDescriptor? _selected;
    [ObservableProperty] private string _statusText = "";

    public Preset? Result { get; private set; }
    public event EventHandler? CloseRequested;

    public PresetPickerViewModel(PresetService presets, string strategyName)
    {
        _presets = presets;
        _strategyName = strategyName;

        ShowAll = false;
        Reload();
    }

    partial void OnShowAllChanged(bool value) => Reload();

    private void Reload()
    {
        Presets.Clear();

        var source = ShowAll
            ? _presets.ListAll()
            : _presets.ListForStrategy(_strategyName);

        foreach (var p in source) Presets.Add(p);

        StatusText = ShowAll
            ? $"{Presets.Count} presets (all strategies)"
            : $"{Presets.Count} presets for {_strategyName}";
    }

    [RelayCommand]
    private void Load()
    {
        if (Selected == null)
        {
            StatusText = "No preset selected.";
            return;
        }

        var preset = _presets.Load(Selected.FilePath);
        if (preset == null)
        {
            StatusText = $"Failed to load {Selected.PresetName}.";
            return;
        }

        Result = preset;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Delete()
    {
        if (Selected == null)
        {
            StatusText = "No preset selected.";
            return;
        }

        _presets.Delete(Selected.FilePath);
        Selected = null;
        Reload();
    }

    [RelayCommand]
    private void Cancel()
    {
        Result = null;
        CloseRequested?.Invoke(this, EventArgs.Empty);
    }
}