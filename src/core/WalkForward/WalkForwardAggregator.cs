using System;
using System.Collections.Generic;
using System.Linq;

namespace backs2.Core.WalkForward;

/// <summary>
/// Pure summary arithmetic over a set of window results. Means and medians
/// are taken over <c>Success == true</c> windows only; failed windows still
/// contribute to <see cref="WalkForwardSummary.WindowCount"/>.
///
/// <para>The stitched equity curve is built by offsetting each window's
/// window-local curve (which starts at 0) by the cumulative net PnL of every
/// window before it, then concatenating in chronological order. The planner
/// guarantees OOS ranges do not overlap (step &gt;= oosLen), so this is a
/// genuine continuous equity curve, not an average of overlapping segments.</para>
/// </summary>
public static class WalkForwardAggregator
{
    /// <summary>Cap on stitched curve points. Charts downsample again at render time.</summary>
    public const int MaxStitchedPoints = 1024;

    /// <summary>Cap on per-window curve points, matching the runner's own cap.</summary>
    public const int MaxWindowCurvePoints = 256;

    /// <summary>Epsilon used to keep the consistency-score denominator sane.</summary>
    private const double Eps = 1e-9;

    public static WalkForwardSummary Aggregate(IReadOnlyList<WalkForwardWindowResult> windows)
    {
        if (windows == null) throw new ArgumentNullException(nameof(windows));

        var ok = windows.Where(w => w.Success).ToList();

        if (ok.Count == 0)
        {
            return new WalkForwardSummary(
                WindowCount: windows.Count,
                SuccessfulWindows: 0,
                MeanOosObjective: 0.0,
                MedianOosObjective: 0.0,
                MeanDegradation: 0.0,
                StitchedOosNetPnl: 0.0,
                StitchedOosMaxDrawdown: 0.0,
                PctWindowsPositiveOosPnl: 0.0,
                PctWindowsBeatNull: 0.0,
                ConsistencyScore: 0.0,
                StitchedEquityCurve: Array.Empty<double>());
        }

        double meanObj = ok.Average(w => w.OosObjective);
        double medianObj = Median(ok.Select(w => w.OosObjective).ToList());
        double meanDeg = ok.Average(w => w.Degradation);
        double stitchedPnl = ok.Sum(w => w.OosNetPnl);

        double pctPositive = 100.0 * ok.Count(w => w.OosNetPnl > 0) / ok.Count;
        double pctBeatNull = 100.0 * ok.Count(w => w.OosBeatNull) / ok.Count;

        var stitched = BuildStitchedCurve(ok, out bool stitchedFromCurves);
        double stitchedDd = stitchedFromCurves
            ? MaxDrawdown(stitched)
            : ok.Max(w => w.OosMaxDrawdown);

        double consistency = ComputeConsistency(ok, pctPositive);

        return new WalkForwardSummary(
            WindowCount: windows.Count,
            SuccessfulWindows: ok.Count,
            MeanOosObjective: meanObj,
            MedianOosObjective: medianObj,
            MeanDegradation: meanDeg,
            StitchedOosNetPnl: stitchedPnl,
            StitchedOosMaxDrawdown: stitchedDd,
            PctWindowsPositiveOosPnl: pctPositive,
            PctWindowsBeatNull: pctBeatNull,
            ConsistencyScore: consistency,
            StitchedEquityCurve: stitched);
    }

    // ------------------------------------------------------------------
    // Curve stitching
    // ------------------------------------------------------------------

    private static double[] BuildStitchedCurve(
        IReadOnlyList<WalkForwardWindowResult> ok,
        out bool fromCurves)
    {
        fromCurves = false;
        if (ok.Count == 0) return Array.Empty<double>();

        bool anyCurve = ok.Any(w => w.OosEquityCurve != null && w.OosEquityCurve.Length > 0);
        if (!anyCurve) return Array.Empty<double>();

        var concat = new List<double>(ok.Count * MaxWindowCurvePoints);
        double cumulativeOffset = 0.0;

        foreach (var w in ok)
        {
            var curve = w.OosEquityCurve;
            if (curve == null || curve.Length == 0)
            {
                // A window without a stored curve still advances the offset by
                // its net PnL so subsequent windows land at the right level.
                cumulativeOffset += w.OosNetPnl;
                continue;
            }

            for (int i = 0; i < curve.Length; i++)
                concat.Add(cumulativeOffset + curve[i]);

            cumulativeOffset += w.OosNetPnl;
        }

        if (concat.Count == 0) return Array.Empty<double>();
        fromCurves = true;

        return concat.Count <= MaxStitchedPoints
            ? concat.ToArray()
            : Downsample(concat, MaxStitchedPoints);
    }

    /// <summary>Linear interpolation downsample to exactly <paramref name="target"/> points.</summary>
    public static double[] Downsample(IReadOnlyList<double> src, int target)
    {
        if (src == null) throw new ArgumentNullException(nameof(src));
        if (target <= 0) throw new ArgumentOutOfRangeException(nameof(target));
        if (src.Count == 0) return Array.Empty<double>();
        if (src.Count <= target) return src.ToArray();

        var dst = new double[target];
        double step = (src.Count - 1.0) / (target - 1.0);

        for (int i = 0; i < target; i++)
        {
            double srcPos = i * step;
            int lo = (int)Math.Floor(srcPos);
            int hi = Math.Min(lo + 1, src.Count - 1);
            double t = srcPos - lo;
            dst[i] = src[lo] * (1.0 - t) + src[hi] * t;
        }

        return dst;
    }

    private static double MaxDrawdown(IReadOnlyList<double> curve)
    {
        if (curve == null || curve.Count == 0) return 0.0;

        double peak = curve[0];
        double maxDd = 0.0;
        for (int i = 1; i < curve.Count; i++)
        {
            if (curve[i] > peak) peak = curve[i];
            double dd = peak - curve[i];
            if (dd > maxDd) maxDd = dd;
        }
        return maxDd;
    }

    // ------------------------------------------------------------------
    // Statistics helpers
    // ------------------------------------------------------------------

    private static double Median(List<double> values)
    {
        if (values.Count == 0) return 0.0;
        values.Sort();
        int mid = values.Count / 2;
        return (values.Count & 1) == 1
            ? values[mid]
            : 0.5 * (values[mid - 1] + values[mid]);
    }

    /// <summary>
    /// Consistency = (fraction positive) * (1 - CV), where CV is the
    /// coefficient of variation of OosNetPnl over successful windows,
    /// clamped to [0, 1]. Requires 2+ successful windows; below that it is 0
    /// because a single window has no distribution.
    /// </summary>
    private static double ComputeConsistency(
        IReadOnlyList<WalkForwardWindowResult> ok, double pctPositive)
    {
        if (ok.Count < 2) return 0.0;

        double mean = ok.Average(w => w.OosNetPnl);
        double variance = ok.Sum(w => (w.OosNetPnl - mean) * (w.OosNetPnl - mean)) / (ok.Count - 1);
        double stddev = Math.Sqrt(variance);
        double cv = stddev / (Math.Abs(mean) + Eps);

        double score = (pctPositive / 100.0) * (1.0 - Math.Min(1.0, cv));
        if (score < 0.0) score = 0.0;
        if (score > 1.0) score = 1.0;
        return score;
    }
}