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
/// <param name="Slices">Source time slices.</param>
/// <param name="TargetDurationSlices">Slices each simulated run must produce.</param>
/// <param name="Iterations">Number of independent runs.</param>
/// <param name="Seed">Seed for reproducible runs; null means non-deterministic.</param>
public sealed record MonteCarloDispatchRequest(
    TimeSlice[] Slices,
    int TargetDurationSlices,
    int Iterations,
    int? Seed = null);

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
    /// Dispatches a Monte Carlo workload and returns one terminal balance per iteration.
    /// </summary>
    /// <returns>
    /// An array of length <c>request.Iterations</c>, or empty when the request is
    /// degenerate. Indexed by iteration — the retry/aggregation shape stays the
    /// caller's business.
    /// </returns>
    Task<double[]> DispatchMonteCarloAsync(
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
