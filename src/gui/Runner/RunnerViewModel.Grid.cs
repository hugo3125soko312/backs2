using System;

namespace backs2.ViewModels;

public partial class RunnerViewModel
{
    private void OnParametersCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
        {
            foreach (RunnerParameterViewModel p in e.OldItems)
                p.PropertyChanged -= OnParameterPropertyChanged;
        }
        if (e.NewItems != null)
        {
            foreach (RunnerParameterViewModel p in e.NewItems)
                p.PropertyChanged += OnParameterPropertyChanged;
        }
        RecomputeCombinations();
    }

    private void OnParameterPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        bool gridField = e.PropertyName is nameof(RunnerParameterViewModel.MinText)
                        or nameof(RunnerParameterViewModel.MaxText)
                        or nameof(RunnerParameterViewModel.StepText);

        if (gridField)
        {
            if (!_suppressGridPersist && sender is RunnerParameterViewModel p)
                PersistGridRange(p);
        }

        if (e.PropertyName is nameof(RunnerParameterViewModel.ValueCount)
            or nameof(RunnerParameterViewModel.MinText)
            or nameof(RunnerParameterViewModel.MaxText)
            or nameof(RunnerParameterViewModel.StepText))
        {
            RecomputeCombinations();
        }
    }

    private static string GridKey(string strategyName, string paramName)
        => $"{strategyName}|{paramName}";

    private void PersistGridRange(RunnerParameterViewModel p)
    {
        if (SelectedStrategy == null) return;

        var key = GridKey(SelectedStrategy.Name, p.Name);
        var serialized = p.GridRangeSerialized();

        // Treat a fully-empty range as removal so we don't accumulate empty entries.
        if (serialized == "||")
            _config.Settings.OptimizerGridRanges.Remove(key);
        else
            _config.Settings.OptimizerGridRanges[key] = serialized;

        _config.SaveConfig();
    }

    private void PersistAllGridRanges()
    {
        if (SelectedStrategy == null) return;

        foreach (var p in Parameters)
        {
            var key = GridKey(SelectedStrategy.Name, p.Name);
            var serialized = p.GridRangeSerialized();

            if (serialized == "||")
                _config.Settings.OptimizerGridRanges.Remove(key);
            else
                _config.Settings.OptimizerGridRanges[key] = serialized;
        }

        _config.SaveConfig();
    }

    private void RecomputeCombinations()
    {
        long total = 1;
        foreach (var p in Parameters)
        {
            total *= p.ValueCount;
            if (total > int.MaxValue) { total = int.MaxValue; break; }
        }

        TotalCombinations = (int)total;
        ExceedsCap = TotalCombinations > Cap;

        if (Parameters.Count == 0)
        {
            CombinationSummary = "";
        }
        else if (ExceedsCap)
        {
            CombinationSummary = $"Grid: {TotalCombinations:N0} combinations exceeds cap {Cap:N0} - will sample";
        }
        else
        {
            CombinationSummary = $"Grid: {TotalCombinations:N0} combination(s)";
        }
    }

    /// <summary>
    /// Grid sampling cap. Editable from the toolbar and persisted to
    /// AppSettings.OptimizerMaxCombinations, so the 500 default is a starting
    /// point rather than a hard ceiling.
    /// </summary>
    public int Cap
    {
        get => _config.Settings.OptimizerMaxCombinations > 0
            ? _config.Settings.OptimizerMaxCombinations
            : 500;
        set
        {
            int clamped = Math.Max(1, value);
            if (clamped == Cap) return;

            _config.Settings.OptimizerMaxCombinations = clamped;
            _config.SaveConfig();
            OnPropertyChanged(nameof(Cap));
            RecomputeCombinations();
        }
    }
}