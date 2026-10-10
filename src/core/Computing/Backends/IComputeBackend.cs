using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Computing;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// Backend abstraction for time-domain bootstrap simulations (ROADMAP Phase 28).
///
/// Two implementations exist: <see cref="CpuBackend"/> (Parallel.For over the
/// iteration range) and <see cref="GpuBackend"/> (ComputeSharp / DirectX 12, one
/// GPU thread per simulation). The scheduler in
/// <see cref="CpuComputeDispatcher"/> picks between them by
/// <see cref="IsAccelerated"/>, so callers never name a device.
///
/// Contract:
///   - Implementations never throw for valid input; degenerate input yields an
///     empty result rather than an exception, and a mid-chunk device failure
///     falls back rather than propagating.
///   - Cancellation is observed between iterations, never mid-iteration, so a
///     cancelled call leaves no partial state behind.
///   - Given the same (slices, targetDurationSlices, iterations, seed) the result
///     is reproducible on both backends. The two backends do not share an RNG
///     implementation (System.Random vs a GPU PRNG), so reproducibility is
///     within a backend; across backends the aggregates are statistically
///     equivalent, not bit-identical.
///   - The result carries exactly max(iterations, 0) entries and is indexed by
///     iteration number starting at zero; the caller owns it outright.
///   - RNG split: the RNG for iteration <c>i</c> is derived from the
///     <em>absolute</em> batch index, never a chunk-local one, so chunk
///     boundaries never move a result.
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
    /// returns their complete per-iteration results.
    /// </summary>
    /// <param name="slices">Source time slices to resample from. A null or empty array yields an empty result.</param>
    /// <param name="request">Per-batch simulation parameters. <c>TargetDurationSlices</c> or less yields an empty result.</param>
    /// <param name="iterations">Number of independent runs. Non-positive yields an empty result.</param>
    /// <param name="seed">Seed for reproducible runs; null means non-deterministic.</param>
    /// <param name="retainCurves">True to produce one equity curve per iteration (the caller's retained-curve path).</param>
    /// <param name="maxDegreeOfParallelism">
    /// Upper bound on concurrently executing iterations: 1 is the sequential
    /// fast-path for callers already saturating the machine from outside, -1 (or
    /// any non-positive value) is Environment.ProcessorCount. Always 1 on the CPU
    /// backend; a GPU backend is free to interpret it as a launch bound because
    /// its concurrency comes from the device, not from threads.
    /// </param>
    /// <param name="ct">Observed between iterations.</param>
    Task<MonteCarloDispatchResult> RunSimulationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int iterations,
        int? seed,
        bool retainCurves,
        int maxDegreeOfParallelism,
        CancellationToken ct);

    /// <summary>
    /// Runs one chunk of a chunked Monte Carlo batch — the unit of work the
    /// parent-process scheduler claims off its queue (ROADMAP Phase 28B).
    /// </summary>
    /// <remarks>
    /// This is the seam that lets <c>CpuComputeDispatcher</c> stay backend-agnostic:
    /// the scheduler owns chunking, work-stealing, and result placement, while the
    /// backend owns how a contiguous iteration range actually executes. The CPU
    /// backend runs it on the calling worker thread; the GPU backend dispatches it
    /// as one wavefront.
    ///
    /// Determinism is the contract that makes chunking safe: the RNG for iteration
    /// <c>startIteration + k</c> is derived from that absolute index, never from a
    /// chunk-local index, so results are reproducible and do not move when chunk
    /// boundaries move.
    ///
    /// <paramref name="startIteration"/> is also the iteration index handed to
    /// <see cref="ISimulationKernel.Simulate"/>, so a kernel that derives extra
    /// entropy from the iteration number sees the same value either way.
    /// </remarks>
    /// <param name="slices">Source time slices. A null or empty array yields an empty result.</param>
    /// <param name="request">Per-batch simulation parameters.</param>
    /// <param name="startIteration">Absolute index of this chunk's first iteration within the whole batch.</param>
    /// <param name="iterationCount">Iterations in this chunk. Non-positive yields an empty result.</param>
    /// <param name="seed">Seed for reproducible runs; null means non-deterministic.</param>
    /// <param name="retainCurves">True to produce one equity curve per iteration in this chunk.</param>
    /// <param name="ct">Observed between iterations, exactly as the whole-batch entry point does.</param>
    /// <returns>A result carrying exactly max(<paramref name="iterationCount"/>, 0) entries, indexed locally from 0.</returns>
    Task<MonteCarloDispatchResult> RunIterationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int startIteration,
        int iterationCount,
        int? seed,
        bool retainCurves,
        CancellationToken ct);
}
