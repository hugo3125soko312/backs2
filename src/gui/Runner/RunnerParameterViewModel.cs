using System;
using System.Collections.Generic;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Runner;

namespace backs2.ViewModels;

public partial class RunnerParameterViewModel : ObservableObject
{
    public string Name { get; }
    public string Type { get; }
    public string DefaultValue { get; }
    public IReadOnlyList<string> Options { get; }

    public bool IsBool => Type == "bool";
    public bool IsNumeric => Type == "int" || Type == "double";
    public bool IsString => Type == "string";
    public bool HasOptions => Options.Count > 0;
    public bool IsFreeText => IsString && !HasOptions;
    public bool IsBoolToggle => IsBool;
    public bool IsTextInput => !IsBool && !HasOptions;

    [ObservableProperty] private string _currentValue = "";
    [ObservableProperty] private bool _currentBool;
    [ObservableProperty] private string _minText = "";
    [ObservableProperty] private string _maxText = "";
    [ObservableProperty] private string _stepText = "";

    public double? MinValue     => ParseDouble(MinText);
    public double? MaxValue     => ParseDouble(MaxText);
    public double? IncrementValue => ParseDouble(StepText);

    private static double? ParseDouble(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
            return d;
        return null;
    }

    public bool IsDirty
    {
        get
        {
            var current = EffectiveValue;
            return !string.Equals(current, DefaultValue, StringComparison.Ordinal);
        }
    }

    public RunnerParameterViewModel(StrategyParameter p)
    {
        Name = p.Name;
        Type = p.Type;
        DefaultValue = p.DefaultValue;
        Options = p.Options;
        _currentValue = p.DefaultValue;
        _currentBool = string.Equals(p.DefaultValue, "true", StringComparison.OrdinalIgnoreCase);
    }

    partial void OnCurrentBoolChanged(bool value)
    {
        CurrentValue = value ? "true" : "false";
        OnPropertyChanged(nameof(IsDirty));
    }

    partial void OnCurrentValueChanged(string value)
    {
        OnPropertyChanged(nameof(IsDirty));
    }

    public string EffectiveValue => IsBool ? (CurrentBool ? "true" : "false") : CurrentValue;

    public string ValueCountText
    {
        get
        {
            int n = ValueCount;
            return n <= 1 ? "" : $"-> {n} values";
        }
    }

    public int ValueCount
    {
        get
        {
            if (!IsNumeric) return 1;

            var min = MinValue;
            var max = MaxValue;
            var step = IncrementValue;

            if (!min.HasValue || !max.HasValue || !step.HasValue) return 1;
            if (step.Value <= 0) return 1;
            if (max.Value < min.Value) return 1;

            double range = max.Value - min.Value;
            double stepVal = step.Value;

            if (string.Equals(Type, "int", StringComparison.OrdinalIgnoreCase))
                return (int)Math.Floor(range / stepVal) + 1;

            double raw = range / stepVal;
            return (int)Math.Floor(Math.Round(raw, 6)) + 1;
        }
    }

    public bool HasGridRange => ValueCount > 1;

    partial void OnMinTextChanged(string value) => NotifyGridChanged();
    partial void OnMaxTextChanged(string value) => NotifyGridChanged();
    partial void OnStepTextChanged(string value) => NotifyGridChanged();

    private void NotifyGridChanged()
    {
        OnPropertyChanged(nameof(MinValue));
        OnPropertyChanged(nameof(MaxValue));
        OnPropertyChanged(nameof(IncrementValue));
        OnPropertyChanged(nameof(ValueCount));
        OnPropertyChanged(nameof(ValueCountText));
        OnPropertyChanged(nameof(HasGridRange));
    }

    /// <summary>
    /// Serialises the grid range for persistence. Empty fields stay empty.
    /// </summary>
    public string GridRangeSerialized()
        => $"{MinText}|{MaxText}|{StepText}";

    public void ApplySerializedGridRange(string serialized)
    {
        if (string.IsNullOrEmpty(serialized)) return;

        var parts = serialized.Split('|');
        if (parts.Length >= 1) MinText = parts[0];
        if (parts.Length >= 2) MaxText = parts[1];
        if (parts.Length >= 3) StepText = parts[2];
    }
}