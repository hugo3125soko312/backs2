using System.Collections.Generic;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Metrics;

namespace backs2.ViewModels;

public sealed record MetricInfo(
    string Name,
    string Lag,
    string Category,
    string Description);

public partial class MetricsViewModel : ObservableObject
{
    private readonly MetricRegistry _registry;

    [ObservableProperty]
    private ObservableCollection<MetricInfo> _metrics = new();

    public MetricsViewModel(MetricRegistry registry)
    {
        _registry = registry;
        Reload();
    }

    private void Reload()
    {
        Metrics.Clear();
        var list = new List<MetricInfo>();
        foreach (var m in _registry.Metrics)
        {
            list.Add(new MetricInfo(
                m.Name,
                m.Lag.ToString(),
                m.Category.ToString(),
                m.Description));
        }

        list.Sort(static (a, b) =>
        {
            int c = string.CompareOrdinal(a.Category, b.Category);
            return c != 0 ? c : string.CompareOrdinal(a.Name, b.Name);
        });

        foreach (var item in list)
        {
            Metrics.Add(item);
        }
    }
}