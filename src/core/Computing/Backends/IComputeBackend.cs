using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// Backend abstraction for time-domain bootstrap simulations (ROADMAP Phase 28).
///
/// Two implementations exist: <see cref="CpuBackend"/> (System.Threading.Tasks.Parallel)
/// and <see cref="GpuBackend"/> (ComputeSharp / DirectX 12). The signature is the one
/// specified by Phase 27 — boundary is TimeSlice[] in, terminal balances out.
///
/// The long-term owner of a GPU device is the compute worker process
/// (src/core/Compute/WorkerHost.cs), not the parent UI process. Phase 28B
/// therefore schedules in the parent process against a CPU backend; migrating
/// that scheduler into the worker is a later phase, and the parent process stays
/// CPU-only until it lands.
///
/// Contract:
///   - Implementations never throw for valid input; degenerate input yields an
///     empty result rather than an exception.
///   - Cancellation is observed between iterations, never mid-iteration, so a
///     cancelled call leaves no partial state behind.
///   - Given the same (slices, targetDurationSlices, iterations, seed) the result
///     is reproducible on both backends.
///   - The result array is always of length max(iterations, 0) and is indexed by
///     iteration number; the caller owns it outright.
/// </summary>
public interface IComputeBackend
{
    /// <summary>
    /// Human-readable backend name for logs and the worker's progress stream
    /// (Phase 27C surfaces this to the UI status line as "GPU: &lt;adapter&gt;").
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Gets a value indicating whether this backend can execute on accelerated
    /// hardware right now. A <see cref="GpuBackend"/> that failed its device probe
    /// reports false and delegates to its CPU fallback.
    /// </summary>
    bool IsAccelerated { get; }

    /// <summary>
    /// Runs <paramref name="iterations"/> independent bootstrap simulations and
    /// returns one terminal balance per iteration.
    /// </summary>
    /// <param name="slices">Source time slices to resample from. A null or empty array yields an empty result.</param>
    /// <param name="targetDurationSlices">Number of slices each simulated run must produce. Non-positive yields an empty result.</param>
    /// <param name="iterations">Number of independent runs. Non-positive yields an empty result.</param>
    /// <param name="seed">Seed for reproducible runs; null means non-deterministic. When supplied, per-iteration
    /// RNGs are split exactly as MonteCarloEngine does it (<c>new Random(seed + i)</c>).</param>
    /// <param name="ct">Observed between iterations.</param>
    Task<double[]> RunSimulationsAsync(
        TimeSlice[] slices,
        int targetDurationSlices,
        int iterations,
        int? seed,
        CancellationToken ct);

    /// <summary>
    /// Runs one chunk of a chunked Monte Carlo batch — the unit of work the
    /// parent-process scheduler claims off its queue (ROADMAP Phase 28B).
    /// </summary>
    /// <remarks>
    /// This is the seam that lets <c>CpuComputeDispatcher</c> stay backend-agnostic:
    /// the scheduler owns chunking, work-stealing, and result placement, while the
    /// backend owns how a contiguous iteration range actually executes. The CPU
    /// backend runs it on the calling worker thread; a future accelerated backend
    /// dispatches it as one GPU wavefront.
    ///
    /// Determinism is the contract that makes chunking safe: the RNG for iteration
    /// <c>startIteration + k</c> is derived from that absolute index
    /// (<c>new Random(seed + (startIteration + k))</c>), never from a chunk-local
    /// index, so results are bit-identical to the whole-batch call and do not move
    /// when chunk boundaries move.
    ///
    /// <paramref name="startIteration"/> is also the iteration index handed to
    /// <see cref="ISimulationKernel.SimulateOnce"/>, so a kernel that derives extra
    /// entropy from the iteration number sees the same value either way.
    /// </remarks>
    /// <param name="slices">Source time slices. A null or empty array yields an empty result.</param>
    /// <param name="targetDurationSlices">Slices each simulated run must produce. Non-positive yields an empty result.</param>
    /// <param name="startIteration">Absolute index of this chunk's first iteration within the whole batch.</param>
    /// <param name="iterationCount">Iterations in this chunk. Non-positive yields an empty result.</param>
    /// <param name="seed">Seed for reproducible runs; null means non-deterministic.</param>
    /// <param name="ct">Observed between iterations, exactly as the whole-batch entry point does.</param>
    /// <returns>An array of length max(<paramref name="iterationCount"/>, 0), indexed locally from 0.</returns>
    Task<double[]> RunIterationsAsync(
        TimeSlice[] slices,
        int targetDurationSlices,
        int startIteration,
        int iterationCount,
        int? seed,
        CancellationToken ct);
}
