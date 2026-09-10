using System;
using System.Collections.Generic;
using System.Linq;
using backs2.Core.Models;

namespace backs2.Core.Metrics;

// Base template to reduce boilerplate
public abstract class BaseMetric : IMetric
{
    public abstract string Name { get; }
    public abstract double Calculate(List<Kline> data);
    
    // Helper for common calculations
    protected double GetAverage(List<Kline> data) => (double)data.Average(k => k.Close);
}

public class HighMetric : BaseMetric, IBaseMetric
{
    public override string Name => "SessionHigh";
    public override double Calculate(List<Kline> data) => data.Count == 0 ? 0 : (double)data.Max(k => k.High);
}

public class VwapMetric : BaseMetric, INormalMetric
{
    public override string Name => "VWAP";
    public override double Calculate(List<Kline> data)
    {
        if (data.Count == 0) return 0;
        double vol = (double)data.Sum(k => k.Volume);
        return vol == 0 ? 0 : (double)data.Sum(k => (k.High + k.Low + k.Close) / 3 * k.Volume) / vol;
    }
}

public class ZScoreMetric : BaseMetric, INormalMetric
{
    public override string Name => "ZScore";
    public override double Calculate(List<Kline> data)
    {
        if (data.Count < 2) return 0;
        var prices = data.Select(k => (double)k.Close).ToList();
        double avg = prices.Average();
        double std = Math.Sqrt(prices.Sum(p => Math.Pow(p - avg, 2)) / prices.Count);
        return std == 0 ? 0 : (prices.Last() - avg) / std;
    }
}