using System.Collections.Generic;

namespace backs2.Core.Metrics;

public class MetricRegistry
{
    private readonly Dictionary<string, IMetric> _metrics = new();

    public MetricRegistry()
    {
        // Registration logic
        Register(new HighMetric());
        Register(new VwapMetric());
        Register(new ZScoreMetric());
    }

    public void Register(IMetric metric) => _metrics[metric.Name] = metric;
    
    // This is the method your ViewModel will now call
    public IEnumerable<string> GetAvailableMetrics() => _metrics.Keys;
    
    public IMetric GetMetric(string name) => _metrics[name];
}