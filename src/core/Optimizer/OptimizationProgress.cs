using System;

namespace backs2.Core.Optimizer;

/// <summary>
/// Progress event emitted by OptimizationRunner at each stage boundary and
/// at each version within a stage. Consumed by the Runner's UI progress bar
/// and ETA estimator.
///
/// Phases, in order:
///   "Grid"       — Cartesian generation + optional sampling (fast)
///   "Backtest"   — parallel backtests across all combinations
///   "Coarse MC"  — sequential MC at coarse iterations per version
///   "Refine MC"  — sequential MC at full iterations on the top-N survivors
///   "Save"       — writing the batch archive to disk
///   "Done"       — terminal event
///
/// EtaSeconds is null until the estimator has warmed up (first 5 versions)
/// or when the remaining work is unknown.
/// </summary>
public sealed record OptimizationProgress(
    string Phase,
    int Completed,
    int Total,
    string Message,
    double? EtaSeconds = null)
{
    /// <summary>
    /// Progress within the current phase, 0..1. This is what <see cref="Fraction"/>
    /// used to be, and it is NOT monotonic across a run: every phase starts at 0.
    /// </summary>
    public double PhaseFraction => Total > 0 ? (double)Completed / Total : 0;

    /// <summary>
    /// Phase weights as a fraction of total expected wall time, derived from the
    /// measured 2026-10-25 run (backtest 28.7%, coarse 63.8%, refine 6.7%,
    /// save 0.5%) and normalised so they sum to 1. Grid is near-instant.
    /// </summary>
    private static double PhaseWeight(string phase) => phase switch
    {
        "Grid"      => 0.005,
        "Backtest"  => 0.290,
        "Coarse MC" => 0.640,
        "Refine MC" => 0.060,
        "Save"      => 0.005,
        "Done"      => 0.000,
        _           => 0.000
    };

    /// <summary>
    /// Cumulative weight of every phase that precedes <paramref name="phase"/>.
    /// </summary>
    private static double PhaseBase(string phase) => phase switch
    {
        "Grid"      => 0.000,
        "Backtest"  => 0.005,
        "Coarse MC" => 0.295,
        "Refine MC" => 0.935,
        "Save"      => 0.995,
        "Done"      => 1.000,
        _           => 0.000
    };

    /// <summary>
    /// Overall run progress, 0..1, monotonic across phase boundaries. Used by the
    /// progress bar; <see cref="PhaseFraction"/> is used nowhere for display.
    /// </summary>
    public double Fraction => Math.Clamp(PhaseBase(Phase) + PhaseFraction * PhaseWeight(Phase), 0.0, 1.0);
}