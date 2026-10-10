using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// CPU backend: <see cref="Parallel.For"/> over iterations (ROADMAP Phase 28).
///
/// This file is deliberately only the parallel skeleton. It does not duplicate
/// <c>MonteCarloEngine.SimulateOnce</c> — the per-iteration state machine arrives
/// through the constructor as an <see cref="ISimulationKernel"/>. Phase 29B wires
/// the real engine kernel in; until then the placeholder kernel below keeps the
/// subsystem resolvable and non-throwing.
///
/// Determinism: RNGs are split identically to <c>MonteCarloEngine.Run</c>
/// (<c>new Random(seed + i)</c> when a seed is supplied, <c>Random.Shared</c>
/// otherwise), so a seeded dispatch is reproducible run over run.
/// </summary>
public sealed class CpuBackend : IComputeBackend
{
    private readonly ISimulationKernel _kernel;

    /// <param name="kernel">
    /// Per-iteration simulation kernel. Defaults to <see cref="PlaceholderKernel"/>
    /// so the type is constructible before Phase 29B wires the real engine in.
    /// </param>
    public CpuBackend(ISimulationKernel? kernel = null)
    {
        _kernel = kernel ?? PlaceholderKernel.Instance;
    }

    /// <inheritdoc/>
    public string Name => "CPU";

    /// <inheritdoc/>
    public bool IsAccelerated => false;

    /// <inheritdoc/>
    public Task<double[]> RunSimulationsAsync(
        TimeSlice[] slices,
        int targetDurationSlices,
        int iterations,
        int? seed,
        CancellationToken ct)
    {
        // Fail soft at the boundary: degenerate input is an empty result, not a throw.
        if (slices == null || slices.Length == 0 || targetDurationSlices <= 0 || iterations <= 0)
            return Task.FromResult(Array.Empty<double>());

        var kernel = _kernel;
        var terminalBalances = new double[iterations];

        // DOP policy matches MonteCarloEngine.Run's default (-1): inner parallelism
        // is Environment.ProcessorCount. The explicit "outer orchestrator already
        // saturates the machine, use a sequential inner loop" knob is a Phase 28B
        // concern — it arrives with the worker's chunked scheduler, which is the
        // caller that actually knows the machine is busy.
        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        void RunIteration(int i)
        {
            var rng = seed.HasValue ? new Random(seed.Value + i) : Random.Shared;

            terminalBalances[i] = kernel.SimulateOnce(
                slices,
                targetDurationSlices,
                rng,
                i);
        }

        if (iterations == 1)
        {
            // One iteration: ThreadPool dispatch costs more than the work itself.
            ct.ThrowIfCancellationRequested();
            RunIteration(0);
        }
        else
        {
            Parallel.For(0, iterations, parallelOptions, RunIteration);
        }

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: CpuBackend ran {iterations} iteration(s) x {targetDurationSlices} slice(s) " +
            $"over {slices.Length} source slice(s) with DOP={parallelOptions.MaxDegreeOfParallelism}" +
            (seed.HasValue ? $" (seed={seed.Value})" : " (unseeded)"));

        return Task.FromResult(terminalBalances);
    }

    /// <summary>
    /// Non-throwing stand-in used until Phase 29B ports the Monte Carlo state
    /// machine. It consumes the RNG exactly as the real kernel will and derives a
    /// deterministic terminal balance from it, which keeps seeded dispatches
    /// reproducible but carries no simulation meaning.
    /// </summary>
    private sealed class PlaceholderKernel : ISimulationKernel
    {
        public static readonly PlaceholderKernel Instance = new();

        public double SimulateOnce(
            TimeSlice[] slices,
            int targetDurationSlices,
            Random rng,
            int iteration)
        {
            double realizedPnl = 0;

            for (int produced = 0; produced < targetDurationSlices; produced++)
                realizedPnl += slices[rng.Next(slices.Length)].RealizedPnl;

            return realizedPnl;
        }
    }
}
