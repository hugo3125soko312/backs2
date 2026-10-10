using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Overview;

namespace backs2.Core.Computing;

/// <summary>
/// Request for one Monte Carlo dispatch.
///
/// A record rather than a long parameter list because the same shape has to cross
/// the worker IPC boundary in Phase 28B, where <c>WorkerEnvelope</c> carries it as
/// JSON. Keeping it a plain data record means phase 28B can serialize it without
/// reshaping this surface.
/// </summary>
/// <param name="Simulation">Per-run simulation parameters (floors, target, block size, drawdown type).</param>
/// <param name="Slices">Source time slices.</param>
/// <param name="Iterations">Number of independent runs.</param>
/// <param name="Seed">Seed for reproducible runs; null means non-deterministic.</param>
/// <param name="RetainCurves">
/// True when the caller consumes one equity curve per iteration. False for
/// passes that only read scalars — it is the difference between a 43 MB output
/// and a few hundred kilobytes on the Phase 29A workload.
/// </param>
/// <param name="MaxDegreeOfParallelism">
/// Upper bound on concurrently executing iterations, normalized exactly as
/// <c>MonteCarloEngine.Run</c> normalizes its inner degree: 1 is the sequential
/// fast-path for a caller that already saturates the machine from outside, N &gt; 1
/// is an exact thread count, and any non-positive value is
/// Environment.ProcessorCount.
/// </param>
public sealed record MonteCarloDispatchRequest(
    MonteCarloSimulationRequest Simulation,
    TimeSlice[] Slices,
    int Iterations,
    int? Seed = null,
    bool RetainCurves = false,
    int MaxDegreeOfParallelism = -1);

/// <summary>
/// Parent-process façade over the unified computing subsystem (ROADMAP Phase 28A).
///
/// This is the only compute entry point the rest of the app sees. It is
/// domain-typed on purpose: callers ask for "a Monte Carlo dispatch" or "a backtest
/// batch", not for "a CUDA call". That keeps the Phase 28B worker-IPC proxy an
/// implementation detail — the proxy will implement this same interface and wrap
/// each method into a <c>WorkerEnvelope</c> JSON payload without a caller-side change.
///
/// Interface note: this façade deliberately carries no reference to
/// <c>WorkerIpc</c> / <c>WorkerEnvelope</c> / <c>JobConfig</c>. Phase 28B crosses that
/// boundary; Phase 28A must not preclude it, so the method shapes stay
/// domain-typed and cancellation-token driven.
/// </summary>
public interface IComputeDispatcher
{
    /// <summary>
    /// Dispatches a Monte Carlo workload and returns its complete per-iteration
    /// results — the nine parallel arrays plus the optional equity curves.
    /// </summary>
    /// <returns>
    /// A result carrying <c>request.Iterations</c> iterations, or fewer when the
    /// dispatch was cancelled or degenerate. Results are truncated at a chunk
    /// boundary, never padded, so every entry is a simulation that ran.
    /// </returns>
    Task<MonteCarloDispatchResult> DispatchMonteCarloAsync(
        MonteCarloDispatchRequest request,
        CancellationToken ct);

    /// <summary>
    /// Dispatches a batch of independent backtest jobs (optimizer parameter
    /// combinations; Phase 29C) and returns one score per job.
    ///
    /// These jobs are non-vectorizable — strategy scripts run through Roslyn — so
    /// they route strictly to the CPU backend (<c>Parallel.For</c>), never to the
    /// scheduler queue. The <c>double[]</c> return is the score channel: the caller
    /// owns the mapping from index to combination.
    /// </summary>
    Task<double[]> DispatchBacktestBatchAsync(
        int jobCount,
        CancellationToken ct);
}
