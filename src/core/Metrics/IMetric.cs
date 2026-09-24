// src\core\Metrics\IMetric.cs
using System.Collections.Generic;
using backs2.Core.Models;

namespace backs2.Core.Metrics;

public enum MetricLag { Leading, Lagging, Mixed }
public enum MetricCategory { Trend, Momentum, Volatility, Volume, Support, Breadth, Statistical }

public interface IMetric
{
    string Name { get; }
    MetricLag Lag { get; }
    MetricCategory Category { get; }
    string Description { get; }
    
    // Calculate returns a double result based on a slice of Klines
    double Calculate(List<Kline> data);
}