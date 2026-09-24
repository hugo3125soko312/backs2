// src\core\Metrics\MetricDefinitions.cs
using System;
using System.Collections.Generic;
using backs2.Core.Models;

namespace backs2.Core.Metrics;

public abstract class BaseMetric : IMetric
{
    public abstract string Name { get; }
    public abstract MetricLag Lag { get; }
    public abstract MetricCategory Category { get; }
    public abstract string Description { get; }
    public abstract double Calculate(List<Kline> data);
}

// ---------------------------------------------------------
// VWAP (Volume Weighted Average Price)
// ---------------------------------------------------------
public sealed class VwapMetric : BaseMetric
{
    public override string Name => "VWAP";
    public override MetricLag Lag => MetricLag.Lagging;
    public override MetricCategory Category => MetricCategory.Volume;
    public override string Description => "Volume Weighted Average Price for the provided period.";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count == 0) return 0;

        double cumVol = 0;
        double cumVolPrice = 0;

        for (int i = 0; i < data.Count; i++)
        {
            var k = data[i];
            double typicalPrice = (k.High + k.Low + k.Close) / 3.0;
            cumVol += k.Volume;
            cumVolPrice += typicalPrice * k.Volume;
        }

        return cumVol == 0 ? 0 : cumVolPrice / cumVol;
    }
}

// ---------------------------------------------------------
// CVD (Cumulative Volume Delta) - CLV x Volume approximation
// ---------------------------------------------------------
public sealed class CvdMetric : BaseMetric
{
    public override string Name => "CVD";
    public override MetricLag Lag => MetricLag.Leading;
    public override MetricCategory Category => MetricCategory.Volume;
    public override string Description => "Cumulative Volume Delta approximated as CLV x Volume (Close Location Value weighted buying/selling pressure).";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count == 0) return 0;

        double cvd = 0;

        for (int i = 0; i < data.Count; i++)
        {
            var k = data[i];
            double range = k.High - k.Low;
            if (range <= 0) continue;

            // CLV in [-1, +1]: where the close sits within the bar's range.
            // +1 = closed at high (buyers), -1 = closed at low (sellers).
            double clv = (2.0 * (k.Close - k.Low) / range) - 1.0;
            cvd += clv * k.Volume;
        }

        return cvd;
    }
}

// ---------------------------------------------------------
// RSI (Relative Strength Index) - 14 period, Wilder smoothing
// ---------------------------------------------------------
public sealed class RsiMetric : BaseMetric
{
    private const int Period = 14;

    public override string Name => "RSI";
    public override MetricLag Lag => MetricLag.Lagging;
    public override MetricCategory Category => MetricCategory.Momentum;
    public override string Description => "14-Period Relative Strength Index (Wilder smoothing).";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count <= Period) return 0;

        double gainSum = 0;
        double lossSum = 0;

        for (int i = 1; i <= Period; i++)
        {
            double change = data[i].Close - data[i - 1].Close;
            if (change > 0) gainSum += change;
            else lossSum -= change;
        }

        double avgGain = gainSum / Period;
        double avgLoss = lossSum / Period;

        for (int i = Period + 1; i < data.Count; i++)
        {
            double change = data[i].Close - data[i - 1].Close;
            double gain = change > 0 ? change : 0;
            double loss = change < 0 ? -change : 0;

            avgGain = ((avgGain * (Period - 1)) + gain) / Period;
            avgLoss = ((avgLoss * (Period - 1)) + loss) / Period;
        }

        if (avgLoss == 0 && avgGain == 0) return 50;
        if (avgLoss == 0) return 100;

        double rs = avgGain / avgLoss;
        return 100.0 - (100.0 / (1.0 + rs));
    }
}

// ---------------------------------------------------------
// ATR (Average True Range) - 14 period, Wilder smoothing
// ---------------------------------------------------------
public sealed class AtrMetric : BaseMetric
{
    private const int Period = 14;

    public override string Name => "ATR";
    public override MetricLag Lag => MetricLag.Lagging;
    public override MetricCategory Category => MetricCategory.Volatility;
    public override string Description => "14-Period Average True Range (Wilder smoothing).";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count <= Period) return 0;

        double trSum = 0;
        for (int i = 1; i <= Period; i++)
        {
            trSum += TrueRange(data[i], data[i - 1].Close);
        }

        double atr = trSum / Period;

        for (int i = Period + 1; i < data.Count; i++)
        {
            double tr = TrueRange(data[i], data[i - 1].Close);
            atr = ((atr * (Period - 1)) + tr) / Period;
        }

        return atr;
    }

    private static double TrueRange(Kline k, double prevClose)
    {
        double hl = k.High - k.Low;
        double hpc = Math.Abs(k.High - prevClose);
        double lpc = Math.Abs(k.Low - prevClose);
        return Math.Max(hl, Math.Max(hpc, lpc));
    }
}

// ---------------------------------------------------------
// Session High - highest High across the provided series
// ---------------------------------------------------------
public sealed class SessionHighMetric : BaseMetric
{
    public override string Name => "SessionHigh";
    public override MetricLag Lag => MetricLag.Lagging;
    public override MetricCategory Category => MetricCategory.Support;
    public override string Description => "Highest High across the provided session series.";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count == 0) return 0;

        double high = data[0].High;
        for (int i = 1; i < data.Count; i++)
        {
            if (data[i].High > high) high = data[i].High;
        }
        return high;
    }
}

// ---------------------------------------------------------
// Z-Score - standard score of the last Close vs the series
// ---------------------------------------------------------
public sealed class ZScoreMetric : BaseMetric
{
    public override string Name => "ZScore";
    public override MetricLag Lag => MetricLag.Mixed;
    public override MetricCategory Category => MetricCategory.Statistical;
    public override string Description => "Standard score of the latest Close relative to the series mean and population standard deviation.";

    public override double Calculate(List<Kline> data)
    {
        if (data == null || data.Count < 2) return 0;

        int n = data.Count;
        double sum = 0;
        for (int i = 0; i < n; i++) sum += data[i].Close;

        double mean = sum / n;

        double sq = 0;
        for (int i = 0; i < n; i++)
        {
            double d = data[i].Close - mean;
            sq += d * d;
        }

        double variance = sq / n;
        if (variance <= 0) return 0;

        double stddev = Math.Sqrt(variance);
        return (data[n - 1].Close - mean) / stddev;
    }
}

// ---------------------------------------------------------
// Volume Profile Engine - single-pass binning + value area
// ---------------------------------------------------------
internal static class VolumeProfileEngine
{
    public static (double POC, double VAH, double VAL) CalculateProfile(
        List<Kline> data,
        int bins = 50,
        double valueAreaPct = 0.70)
    {
        if (data == null || data.Count == 0) return (0, 0, 0);
        if (bins < 2) bins = 2;
        if (valueAreaPct <= 0 || valueAreaPct >= 1) valueAreaPct = 0.70;

        // Pass 1: find high, low, total volume in one sweep.
        double min = data[0].Low;
        double max = data[0].High;
        double totalVolume = 0;

        for (int i = 0; i < data.Count; i++)
        {
            var k = data[i];
            if (k.Low < min) min = k.Low;
            if (k.High > max) max = k.High;
            totalVolume += k.Volume;
        }

        if (max <= min) return (max, max, max);
        if (totalVolume <= 0) return (min, max, min);

        double binSize = (max - min) / bins;
        double[] profile = new double[bins];

        // Pass 2: distribute each candle's volume across the bins it spans.
        for (int i = 0; i < data.Count; i++)
        {
            var k = data[i];
            if (k.Volume <= 0) continue;

            int startBin = (int)((k.Low - min) / binSize);
            int endBin = (int)((k.High - min) / binSize);

            if (startBin < 0) startBin = 0;
            else if (startBin >= bins) startBin = bins - 1;

            if (endBin < 0) endBin = 0;
            else if (endBin >= bins) endBin = bins - 1;

            int spanned = endBin - startBin + 1;
            double volPerBin = k.Volume / spanned;

            for (int b = startBin; b <= endBin; b++)
            {
                profile[b] += volPerBin;
            }
        }

        // POC: bin with the highest accumulated volume.
        int pocIndex = 0;
        double maxVol = profile[0];
        for (int i = 1; i < bins; i++)
        {
            if (profile[i] > maxVol)
            {
                maxVol = profile[i];
                pocIndex = i;
            }
        }

        // Value area: expand outward from POC until target percent is reached.
        double targetVaVolume = totalVolume * valueAreaPct;
        double vaVolume = profile[pocIndex];
        int upperIndex = pocIndex;
        int lowerIndex = pocIndex;

        while (vaVolume < targetVaVolume && (upperIndex < bins - 1 || lowerIndex > 0))
        {
            double upVol = upperIndex < bins - 1 ? profile[upperIndex + 1] : -1.0;
            double downVol = lowerIndex > 0 ? profile[lowerIndex - 1] : -1.0;

            if (upVol >= downVol && upVol >= 0)
            {
                upperIndex++;
                vaVolume += upVol;
            }
            else if (downVol >= 0)
            {
                lowerIndex--;
                vaVolume += downVol;
            }
            else
            {
                break;
            }
        }

        double poc = min + (pocIndex * binSize) + (binSize * 0.5);
        double vah = min + ((upperIndex + 1) * binSize);
        double val = min + (lowerIndex * binSize);

        return (poc, vah, val);
    }
}

public sealed class VolumeProfilePocMetric : BaseMetric
{
    public override string Name => "VP-POC";
    public override MetricLag Lag => MetricLag.Mixed;
    public override MetricCategory Category => MetricCategory.Support;
    public override string Description => "Volume Profile: Point of Control (price level with the highest traded volume).";
    public override double Calculate(List<Kline> data) => VolumeProfileEngine.CalculateProfile(data).POC;
}

public sealed class VolumeProfileVahMetric : BaseMetric
{
    public override string Name => "VP-VAH";
    public override MetricLag Lag => MetricLag.Mixed;
    public override MetricCategory Category => MetricCategory.Support;
    public override string Description => "Volume Profile: Value Area High (upper bound of the 70% volume node).";
    public override double Calculate(List<Kline> data) => VolumeProfileEngine.CalculateProfile(data).VAH;
}

public sealed class VolumeProfileValMetric : BaseMetric
{
    public override string Name => "VP-VAL";
    public override MetricLag Lag => MetricLag.Mixed;
    public override MetricCategory Category => MetricCategory.Support;
    public override string Description => "Volume Profile: Value Area Low (lower bound of the 70% volume node).";
    public override double Calculate(List<Kline> data) => VolumeProfileEngine.CalculateProfile(data).VAL;
}

// ---------------------------------------------------------
// NullMetric - returned by MetricRegistry.GetMetric on miss.
// Calculate always yields 0, so scripts never NRE or throw.
// ---------------------------------------------------------
internal sealed class NullMetric : BaseMetric
{
    public static readonly NullMetric Instance = new();

    private NullMetric() { }

    public override string Name => "(unregistered)";
    public override MetricLag Lag => MetricLag.Mixed;
    public override MetricCategory Category => MetricCategory.Statistical;
    public override string Description => "Null-object metric returned when a requested metric name is not registered. Calculate returns 0.";
    public override double Calculate(List<Kline> data) => 0;
}