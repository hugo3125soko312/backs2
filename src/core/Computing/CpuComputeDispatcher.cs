using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Computing.Backends;

namespace backs2.Core.Computing;

/// <summary>
/// In-process dispatcher (ROADMAP Phase 28A). Resolves <see cref="IComputeBackend"/>
/// from DI and delegates; it holds no compute logic of its own.
///
/// Phase 28B replaces this registration with the worker-IPC proxy, which
/// implements <see cref="IComputeDispatcher"/> by wrapping each call into a
/// <c>WorkerEnvelope</c> and sending it through <c>ComputeWorkerManager</c>. No
/// caller changes when that lands.
/// </summary>
public sealed class CpuComputeDispatcher : IComputeDispatcher
{
    private readonly IComputeBackend _backend;

    public CpuComputeDispatcher(IComputeBackend backend)
    {
        _backend = backend;
    }

    /// <summary>
    /// Gets the backend this dispatcher delegates to — the worker's chosen
    /// backend once Phase 28B lands, the in-process CPU backend until then.
    /// </summary>
    public IComputeBackend Backend => _backend;

    /// <inheritdoc/>
    public Task<double[]> DispatchMonteCarloAsync(
        MonteCarloDispatchRequest request,
        CancellationToken ct)
    {
        if (request == null)
            return Task.FromResult(Array.Empty<double>());

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: dispatch MonteCarlo via {_backend.Name} " +
            $"({request.Iterations} iteration(s), {request.TargetDurationSlices} slice(s), " +
            $"seed={(request.Seed.HasValue ? request.Seed.Value.ToString() : "none")})");

        return _backend.RunSimulationsAsync(
            request.Slices,
            request.TargetDurationSlices,
            request.Iterations,
            request.Seed,
            ct);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Phase 28A runs the batch in-process so the interface is exercisable end to
    /// end. Phase 29C replaces the body with a real batched dispatch to the worker,
    /// where it routes strictly to <c>CpuBackend</c>; the signature does not change.
    /// </remarks>
    public Task<double[]> DispatchBacktestBatchAsync(
        int jobCount,
        CancellationToken ct)
    {
        if (jobCount <= 0)
            return Task.FromResult(Array.Empty<double>());

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: dispatch backtest batch ({jobCount} job(s)) — CPU-only workload");

        var scores = new double[jobCount];

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        Parallel.For(0, jobCount, parallelOptions, i =>
        {
            // Skeleton only: Phase 29C supplies the per-combination backtest that
            // produces this score. The loop shape is the part that must not change
            // — non-vectorizable jobs never enter a GPU queue.
            scores[i] = i;
        });

        return Task.FromResult(scores);
    }
}
