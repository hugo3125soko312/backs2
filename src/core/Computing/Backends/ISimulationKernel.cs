using System;
using System.Threading;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// The per-iteration kernel a backend schedules. This is the seam that keeps the
/// parallel skeleton (<see cref="CpuBackend"/>) separate from the Monte Carlo state
/// machine: Phase 29B supplies the real port of <c>MonteCarloEngine.SimulateOnce</c>
/// through this interface (or an equivalent ComputeSharp <c>[Shader]</c> kernel on
/// the GPU side), and nothing in this folder needs to change.
///
/// Implementations must be:
///   - pure with respect to their inputs — no shared mutable state, never static
///     state that assumes a single thread;
///   - safe to invoke concurrently from many threads (T)Parallel.For;
///   - deterministic for a given (<paramref name="rng"/>, iteration index), which
///     is why the RNG is injected per iteration rather than captured.
/// </summary>
public interface ISimulationKernel
{
    /// <summary>
    /// Simulates exactly one run and returns its terminal balance.
    /// </summary>
    /// <param name="slices">Source slices.</param>
    /// <param name="targetDurationSlices">Slices to produce for this run.</param>
    /// <param name="rng">The RNG dedicated to this iteration; never shared across iterations.</param>
    /// <param name="iteration">Zero-based iteration index, for kernels that derive extra entropy from it.</param>
    double SimulateOnce(
        TimeSlice[] slices,
        int targetDurationSlices,
        Random rng,
        int iteration);
}
