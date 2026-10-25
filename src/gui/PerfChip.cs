using CommunityToolkit.Mvvm.ComponentModel;

namespace backs2.ViewModels;

/// <summary>
/// One metric chip in the status bar readout (ROADMAP Phase 36D).
///
/// Each metric colours independently: a breach on CPU must not paint FPS red.
/// That is why this is a collection of per-metric items rather than a single
/// formatted string with one boolean.
/// </summary>
public partial class PerfChip : ObservableObject
{
    [ObservableProperty]
    private string _label = "";

    [ObservableProperty]
    private string _value = "";

    [ObservableProperty]
    private bool _breached;

    public string Text => Label + " " + Value;

    partial void OnLabelChanged(string value) => OnPropertyChanged(nameof(Text));
    partial void OnValueChanged(string value) => OnPropertyChanged(nameof(Text));
}