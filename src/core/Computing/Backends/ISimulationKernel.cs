using System;
using backs2.Core.Computing;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// The per-iteration kernel a backend schedules — the seam that keeps the
/// parallel skeleton (<see cref="CpuBackend"/>) separate from the Monte Carlo
/// state machine, and that lets the same state machine exist twice: once as
/// <see cref="SimulationKernelCpu"/> and once as the ComputeSharp
/// <see cref="SimulationKernelGpu"/> shader.
///
/// <para><b>Contract.</b> An implementation must be pure with respect to its
/// inputs — no shared mutable state, no static state that assumes a single
/// thread — safe to invoke concurrently from many threads, and deterministic for
/// a given (<paramref name="rng"/> state, <paramref name="iteration"/>) pair.
/// The RNG is injected per iteration rather than captured for exactly that
/// reason.</para>
///
/// <para><b>Why the RNG is not <c>System.Random</c>.</b> The CPU kernel splits
/// RNGs exactly the way <c>MonteCarloEngine.Run</c> always has
/// (<c>new Random(seed + i)</c>). A GPU shader cannot instantiate
/// <c>System.Random</c>, so the shader uses a counter-based Philox-4x32 PRNG
/// seeded from the same (seed, iteration) pair. The two streams are therefore
/// <em>not</em> bit-identical; the contract is statistical equivalence of the
/// aggregates, not bit-exactness across backends. Within one backend, the same
/// seed always produces the same result.</para>
/// </summary>
public interface ISimulationKernel
{
    /// <summary>
    /// Simulates exactly one run and returns its complete outcome.
    /// </summary>
    /// <param name="slices">Source slices. Never null or empty; the backend rejects degenerate input first.</param>
    /// <param name="request">Per-batch simulation parameters.</param>
    /// <param name="rng">The RNG dedicated to this iteration; never shared across iterations.</param>
    /// <param name="iteration">Absolute iteration index within the batch, for kernels that derive entropy from it.</param>
    /// <param name="curve">
    /// Optional per-run equity curve of length <c>request.TargetDurationSlices</c>.
    /// When non-null the kernel writes the running balance into it at the same
    /// point the engine does; when null the kernel writes nothing.
    /// </param>
    MonteCarloIterationOutcome Simulate(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        Random rng,
        int iteration,
        double[]? curve);
}
