using System;
using System.Diagnostics;
using System.Globalization;

namespace backs2.Core.Optimizer;

/// <summary>
/// Dead-simple countdown timer for an optimization run.
///
/// There is no sampling, no blending, no adaptive anything. One estimate
/// is computed once from the run's shape (combination count, refine-top-N,
/// iteration counts) using the tunable per-unit constants below, then the
/// number counts down monotonically as wall time elapses. It only ever
/// decreases. When the estimate is exhausted it floors at
/// <see cref="FloorSeconds"/> and stays there for the rest of the run.
///
/// The whole estimate is derived from these five public constants. If the
/// countdown is consistently wrong — too fast, too slow — tune the constant
/// and rerun. Every constant is in wall seconds per unit of work, already
/// divided by whatever parallelism the optimizer applies (so
/// <see cref="WallSecPerBacktest"/> is the per-combination wall-time when
/// the backtest phase is running across all cores).
///
/// The breakdown of the initial estimate is exposed via <see cref="Breakdown"/>
/// and logged once at Start.
/// </summary>
public sealed class EtaEstimator
{
    // ---- Tunables. Wall seconds per unit of work, post-parallel. --------

    /// <summary>Wall seconds per combination in the backtest phase.</summary>
    public double WallSecPerBacktest { get; init; } = 0.0005;

    /// <summary>Wall seconds per combination in the coarse-MC phase (at the configured coarse iterations).</summary>
    public double WallSecPerCoarseMc { get; init; } = 0.008;

    /// <summary>Wall seconds per survivor in the refine-MC phase, scaled by refineIterations / 1000.</summary>
    public double WallSecPerRefineMc { get; init; } = 0.046;

    /// <summary>Wall seconds per survivor in the null-model pass, scaled by refineIterations / 1000.</summary>
    public double WallSecPerNullMc { get; init; } = 0.046;

    /// <summary>Floor for the reported value. Never returns below this.</summary>
    public double FloorSeconds { get; init; } = 0.5;

    private const double RefineScaleBaselineIterations = 1000.0;

    private Stopwatch? _clock;
    private double _totalSeconds;

    public bool IsRunning => _clock != null;
    public double TotalEstimateSeconds => _totalSeconds;
    public double ElapsedSeconds => _clock?.Elapsed.TotalSeconds ?? 0.0;

    /// <summary>One-line breakdown of how the frozen estimate was assembled.</summary>
    public string Breakdown { get; private set; } = "";

    /// <summary>
    /// Freezes the estimate and starts the countdown clock. Call once at the
    /// start of the run, after the grid is built.
    /// </summary>
    public void Start(
        int combinationCount,
        int refineTopN,
        int coarseIterations,
        int refineIterations)
    {
        _clock = Stopwatch.StartNew();

        double refineScale = coarseIterations > 0
            ? refineIterations / RefineScaleBaselineIterations
            : 1.0;

        double backtestSec = combinationCount * WallSecPerBacktest;
        double coarseSec   = combinationCount * WallSecPerCoarseMc;
        double refineSec   = refineTopN * WallSecPerRefineMc * refineScale;
        double nullSec     = refineTopN * WallSecPerNullMc * refineScale;

        _totalSeconds = backtestSec + coarseSec + refineSec + nullSec;

        Breakdown = string.Format(
            CultureInfo.InvariantCulture,
            "backtest={0:F1}s ({1}x{2:F2}) | coarse={3:F1}s ({1}x{4:F2}) | " +
            "refine={5:F1}s ({6}x{7:F2}x{8:F2}) | null={9:F1}s ({6}x{10:F2}x{8:F2}) | total={11:F1}s",
            backtestSec, combinationCount, WallSecPerBacktest,
            coarseSec, WallSecPerCoarseMc,
            refineSec, refineTopN, WallSecPerRefineMc, refineScale,
            nullSec, WallSecPerNullMc,
            _totalSeconds);
    }

    /// <summary>
    /// Seconds remaining. Monotonically decreasing. Never below
    /// <see cref="FloorSeconds"/>. Returns the floor when Start was never
    /// called.
    /// </summary>
    public double Remaining()
    {
        if (_clock == null) return FloorSeconds;

        double r = _totalSeconds - _clock.Elapsed.TotalSeconds;
        if (double.IsNaN(r) || double.IsInfinity(r) || r < FloorSeconds)
            return FloorSeconds;

        return r;
    }
}