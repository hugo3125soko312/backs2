using System;
using System.Collections.Generic;

namespace backs2.Core.Optimizer;

/// <summary>
/// Estimates the wall-time remaining for an optimization run. Calibrates
/// against the first N samples it receives (default 5), then extrapolates
/// from a moving average of backtest and MC durations.
///
/// Design notes:
/// - Backtests are reported once per version (after the parallel phase).
/// - MC durations include both the coarse and the refine pass, but the
///   refine pass runs on only RefineTopN survivors, so it does not scale
///   with combo count. The estimator takes MC samples from the coarse
///   phase and uses a fixed multiplier for the refine tail.
/// - GpuFactor is 1.0 on CPU; the future GPU path sets it to a factor of
///   the expected MC speedup (e.g. 10-100), and the MC component of the
///   ETA divides by it. The backtest component is never divided — backtests
///   are CPU-bound script execution, not MC.
/// - Returns null when fewer than BlindWindow samples have been observed.
/// </summary>
public sealed class EtaEstimator
{
    public const int BlindWindow = 5;
    private const int MaxSamples = 30;

    private readonly Queue<double> _backtestSamples = new(MaxSamples);
    private readonly Queue<double> _mcSamples = new(MaxSamples);

    /// <summary>
    /// CPU acceleration factor. 1.0 = CPU-only. Future GPU MC path sets this
    /// to the expected MC speedup (e.g. 50.0). Only the MC component of the
    /// estimate is divided by this factor.
    /// </summary>
    public double GpuFactor { get; set; } = 1.0;

    public int SampleCount => Math.Min(_backtestSamples.Count, _mcSamples.Count);

    public bool IsWarmedUp => SampleCount >= BlindWindow;

    public void ReportBacktest(long durationMs)
    {
        Push(_backtestSamples, durationMs);
    }

    public void ReportMc(long durationMs)
    {
        Push(_mcSamples, Math.Max(0, durationMs));
    }

    /// <summary>
    /// Returns the estimated remaining seconds for the given combination
    /// counts, or null when the estimator is not warmed up yet.
    ///
    ///   remainingBacktests = remainingCombos
    ///   remainingCoarseMc  = remainingCombos
    ///   refineTail         = RefineTopN x (RefineIterations / CoarseIterations) x AvgMcPerCoarse
    ///
    /// The refine tail is estimated as a fixed cost — it does not scale with
    /// remainingCombos. When the refine phase has already started, the caller
    /// passes refineRemaining instead of refineTotal.
    /// </summary>
    public double? Estimate(
        int remainingCombos,
        int refineTopN,
        int coarseIterations,
        int refineIterations,
        int refineRemaining)
    {
        if (!IsWarmedUp) return null;
        if (remainingCombos < 0) remainingCombos = 0;
        if (refineRemaining < 0) refineRemaining = 0;

        double avgBacktestMs = Average(_backtestSamples);
        double avgCoarseMcMs = Average(_mcSamples);

        double refinePerVersion = coarseIterations > 0
            ? avgCoarseMcMs * (refineIterations / (double)coarseIterations)
            : avgCoarseMcMs;

        double backtestPortion = remainingCombos * avgBacktestMs;
        double coarsePortion = remainingCombos * avgCoarseMcMs;
        double refinePortion = refineRemaining * refinePerVersion;

        double mcTotal = coarsePortion + refinePortion;
        if (GpuFactor > 0) mcTotal /= GpuFactor;

        double totalMs = backtestPortion + mcTotal;
        return totalMs / 1000.0;
    }

    private static void Push(Queue<double> q, double sample)
    {
        if (sample < 0) sample = 0;
        q.Enqueue(sample);
        while (q.Count > MaxSamples) q.Dequeue();
    }

    private static double Average(Queue<double> q)
    {
        if (q.Count == 0) return 0;
        double sum = 0;
        foreach (var s in q) sum += s;
        return sum / q.Count;
    }
}