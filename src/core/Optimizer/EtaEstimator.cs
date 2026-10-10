using System;
using System.Diagnostics;
using System.Globalization;

namespace backs2.Core.Optimizer;

/// <summary>
/// Self-calibrating ETA estimator (ROADMAP Phase 26C). Replaces the previous
/// frozen-countdown design whose hardcoded per-unit constants produced a 6.1s
/// estimate against a 37.5s actual run. The estimate is recomputed on every
/// Remaining() call from the grid shape captured at Start plus per-unit
/// thread-second moving averages updated by the caller as work completes.
/// Per-unit samples are topology-independent, so the average stays valid
/// across runs on different core counts.
/// </summary>
public sealed class EtaEstimator
{
    public double DefaultThreadSecPerBacktest { get; init; } = 0.100;
    public double DefaultThreadSecPerCoarseSim { get; init; } = 0.00050;
    public double DefaultThreadSecPerRefineSim { get; init; } = 0.00100;
    public double DefaultThreadSecPerNullSim { get; init; } = 0.00100;
    public double DefaultSaveSec { get; init; } = 0.5;
    public double FloorSeconds { get; init; } = 0.5;

    private const double EmaAlpha = 0.125;
    private readonly object _lock = new();
    private Stopwatch? _clock;

    private int _comboCount;
    private int _refineTopN;
    private int _coarseIterations;
    private int _refineIterations;
    private int _backtestOuter = 1;
    private int _coarseOuter = 1;
    private int _refineOuter = 1;

    private double _threadSecPerBacktest;
    private double _threadSecPerCoarseSim;
    private double _threadSecPerRefineSim;
    private double _threadSecPerNullSim;

    public bool IsRunning => _clock != null;
    public double ElapsedSeconds => _clock?.Elapsed.TotalSeconds ?? 0.0;
    public double InitialEstimateSeconds { get; private set; }
    public string Breakdown { get; private set; } = "";

    public void Start(
        int combinationCount,
        int refineTopN,
        int coarseIterations,
        int refineIterations,
        int backtestOuter,
        int coarseOuter,
        int refineOuter)
    {
        lock (_lock)
        {
            _comboCount = Math.Max(0, combinationCount);
            _refineTopN = Math.Max(0, refineTopN);
            _coarseIterations = Math.Max(1, coarseIterations);
            _refineIterations = Math.Max(1, refineIterations);
            _backtestOuter = Math.Max(1, backtestOuter);
            _coarseOuter = Math.Max(1, coarseOuter);
            _refineOuter = Math.Max(1, refineOuter);

            _threadSecPerBacktest = DefaultThreadSecPerBacktest;
            _threadSecPerCoarseSim = DefaultThreadSecPerCoarseSim;
            _threadSecPerRefineSim = DefaultThreadSecPerRefineSim;
            _threadSecPerNullSim = DefaultThreadSecPerNullSim;

            _clock = Stopwatch.StartNew();
            InitialEstimateSeconds = ComputeEstimateLocked();
            Breakdown = FormatBreakdownLocked();
        }
    }

    public void RecordBacktest(double elapsedMs)
    {
        if (elapsedMs <= 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)) return;
        lock (_lock)
        {
            double perVersionSec = elapsedMs / 1000.0;
            _threadSecPerBacktest = Blend(_threadSecPerBacktest, perVersionSec);
        }
    }

    public void RecordCoarseMc(double elapsedMs, int iterations)
    {
        if (elapsedMs <= 0 || iterations <= 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)) return;
        lock (_lock)
        {
            double perSimSec = elapsedMs / 1000.0 / iterations;
            _threadSecPerCoarseSim = Blend(_threadSecPerCoarseSim, perSimSec);
        }
    }

    public void RecordRefineMc(double elapsedMs, int iterations)
    {
        if (elapsedMs <= 0 || iterations <= 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)) return;
        lock (_lock)
        {
            double perSimSec = elapsedMs / 1000.0 / iterations;
            _threadSecPerRefineSim = Blend(_threadSecPerRefineSim, perSimSec);
        }
    }

    public void RecordNullMc(double elapsedMs, int iterations)
    {
        if (elapsedMs <= 0 || iterations <= 0 || double.IsNaN(elapsedMs) || double.IsInfinity(elapsedMs)) return;
        lock (_lock)
        {
            double perSimSec = elapsedMs / 1000.0 / iterations;
            _threadSecPerNullSim = Blend(_threadSecPerNullSim, perSimSec);
        }
    }

    public double EstimateTotal()
    {
        lock (_lock) return ComputeEstimateLocked();
    }

    public double Remaining()
    {
        Stopwatch? clock;
        double total;
        lock (_lock)
        {
            clock = _clock;
            if (clock == null) return FloorSeconds;
            total = ComputeEstimateLocked();
        }
        double r = total - clock.Elapsed.TotalSeconds;
        if (double.IsNaN(r) || double.IsInfinity(r) || r < FloorSeconds) return FloorSeconds;
        return r;
    }

    private static double Blend(double current, double sample)
        => current * (1.0 - EmaAlpha) + sample * EmaAlpha;

    private double ComputeEstimateLocked()
    {
        double backtestSec = (double)_comboCount * _threadSecPerBacktest / _backtestOuter;
        double coarseSec = (double)_comboCount * _coarseIterations * _threadSecPerCoarseSim / _coarseOuter;
        double refineSec = (double)_refineTopN * _refineIterations * _threadSecPerRefineSim / _refineOuter;
        double nullSec = (double)_refineTopN * _refineIterations * _threadSecPerNullSim / _refineOuter;
        return backtestSec + coarseSec + refineSec + nullSec + DefaultSaveSec;
    }

    private string FormatBreakdownLocked()
    {
        double backtestSec = (double)_comboCount * _threadSecPerBacktest / _backtestOuter;
        double coarseSec = (double)_comboCount * _coarseIterations * _threadSecPerCoarseSim / _coarseOuter;
        double refineSec = (double)_refineTopN * _refineIterations * _threadSecPerRefineSim / _refineOuter;
        double nullSec = (double)_refineTopN * _refineIterations * _threadSecPerNullSim / _refineOuter;
        double totalSec = backtestSec + coarseSec + refineSec + nullSec + DefaultSaveSec;
        return string.Format(
            CultureInfo.InvariantCulture,
            "backtest={0:F1}s | coarse={1:F1}s | refine={2:F1}s | null={3:F1}s | save={4:F1}s | total={5:F1}s (W: bt={6} c={7} r={8})",
            backtestSec, coarseSec, refineSec, nullSec, DefaultSaveSec, totalSec,
            _backtestOuter, _coarseOuter, _refineOuter);
    }
}