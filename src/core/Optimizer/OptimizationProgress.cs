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
/// </summary>
public sealed record OptimizationProgress(
    string Phase,
    int Completed,
    int Total,
    string Message)
{
    public double Fraction => Total > 0 ? (double)Completed / Total : 0;
}