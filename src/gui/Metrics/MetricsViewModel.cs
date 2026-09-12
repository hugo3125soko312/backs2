using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Metrics;
using System.Collections.Generic;
using System.Linq; // Added for .ToList()

namespace backs2.ViewModels;

public partial class MetricsViewModel : ObservableObject
{
    private readonly MetricRegistry _registry;

    [ObservableProperty]
    private List<string> _availableMetrics = new();

    public MetricsViewModel(MetricRegistry registry)
    {
        _registry = registry;
        // Updated to use the existing GetAvailableMetrics() method from MetricRegistry
        AvailableMetrics = _registry.GetAvailableMetrics().ToList();
    }
}