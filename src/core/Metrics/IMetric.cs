using System.Collections.Generic;
using backs2.Core.Models;

namespace backs2.Core.Metrics;

public interface IMetric
{
    string Name { get; }
    // Calculate returns a double result based on a slice of Klines
    double Calculate(List<Kline> data);
}

// Marker interface for base data
public interface IBaseMetric : IMetric { }

// Marker interface for derived indicators (VWAP, Z-Score, etc)
public interface INormalMetric : IMetric { }