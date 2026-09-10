using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Metrics;
using System.Collections.ObjectModel;

namespace backs2.ViewModels;

public partial class MetricsViewModel : ObservableObject
{
    private readonly MetricRegistry _registry;

    [ObservableProperty]
    private ObservableCollection<string> _availableMetrics;

    public MetricsViewModel(MetricRegistry registry)
    {
        _registry = registry;
        AvailableMetrics = new ObservableCollection<string>(_registry.GetAvailableMetrics());
    }
}