// src\core\Metrics\MetricRegistry.cs
using System.Collections.Generic;

namespace backs2.Core.Metrics;

public class MetricRegistry
{
    private readonly Dictionary<string, IMetric> _metrics = new();

    public MetricRegistry()
    {
        Register(new VwapMetric());
        Register(new CvdMetric());
        Register(new RsiMetric());
        Register(new AtrMetric());
        Register(new SessionHighMetric());
        Register(new ZScoreMetric());
        Register(new VolumeProfilePocMetric());
        Register(new VolumeProfileVahMetric());
        Register(new VolumeProfileValMetric());
    }

    public void Register(IMetric metric) => _metrics[metric.Name] = metric;

    // Read-only view of every registered metric, including metadata.
    public IReadOnlyCollection<IMetric> Metrics => _metrics.Values;

    // Back-compat: legacy callers that only want names.
    public IEnumerable<string> GetAvailableMetrics() => _metrics.Keys;

    // Never throws, never returns null. Returns NullMetric on miss so
    // strategy scripts (e.g. HelloVwap.cs) can call .Calculate(...) safely.
    public IMetric GetMetric(string name)
    {
        if (name != null && _metrics.TryGetValue(name, out var metric))
        {
            return metric;
        }
        return NullMetric.Instance;
    }

    // For callers that need to distinguish a real hit from a miss.
    public bool TryGetMetric(string name, out IMetric metric)
    {
        if (name != null && _metrics.TryGetValue(name, out var found))
        {
            metric = found;
            return true;
        }
        metric = NullMetric.Instance;
        return false;
    }
}