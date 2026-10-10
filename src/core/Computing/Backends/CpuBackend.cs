using System;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Computing;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// CPU backend: <see cref="Parallel.For"/> over iterations (ROADMAP Phase 28).
///
/// This file is deliberately only the parallel skeleton. It does not contain the
/// Monte Carlo state machine — the per-iteration body arrives through the
/// constructor as an <see cref="ISimulationKernel"/> and defaults to
/// <see cref="SimulationKernelCpu"/>, which is the ported
/// <c>MonteCarloEngine.SimulateOnce</c>.
///
/// Determinism: RNGs are split identically to <c>MonteCarloEngine.Run</c>
/// (<c>new Random(seed + i)</c> with the absolute iteration index when a seed is
/// supplied, <c>Random.Shared</c> otherwise), so a seeded dispatch is
/// reproducible run over run and chunk boundaries never move a result.
/// </summary>
public sealed class CpuBackend : IComputeBackend
{
    private readonly ISimulationKernel _kernel;

    /// <param name="kernel">
    /// Per-iteration simulation kernel. Defaults to <see cref="SimulationKernelCpu"/>.
    /// </param>
    public CpuBackend(ISimulationKernel? kernel = null)
    {
        _kernel = kernel ?? SimulationKernelCpu.Instance;
    }

    /// <inheritdoc/>
    public string Name => "CPU";

    /// <inheritdoc/>
    public bool IsAccelerated => false;

    /// <inheritdoc/>
    public Task<MonteCarloDispatchResult> RunSimulationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int iterations,
        int? seed,
        bool retainCurves,
        int maxDegreeOfParallelism,
        CancellationToken ct)
    {
        // Fail soft at the boundary: degenerate input is an empty result, not a throw.
        if (slices == null || slices.Length == 0 || request.TargetDurationSlices <= 0 || iterations <= 0)
            return Task.FromResult(MonteCarloDispatchResult.Empty);

        // DOP policy matches MonteCarloEngine.Run's normalization: 1 is the
        // sequential fast-path for an outer orchestrator that already saturates
        // the machine, >1 is an exact bound, <=0 is Environment.ProcessorCount.
        int dop = maxDegreeOfParallelism == 1
            ? 1
            : maxDegreeOfParallelism > 1
                ? maxDegreeOfParallelism
                : Environment.ProcessorCount;

        var accumulated = new IterationAccumulator(iterations, request.TargetDurationSlices, retainCurves);

        void RunIteration(int i)
        {
            var rng = CreateRng(seed, i);

            // Each iteration owns its curve slot; no two threads share one.
            var curve = retainCurves ? new double[request.TargetDurationSlices] : null;

            accumulated.Store(
                i,
                _kernel.Simulate(slices, request, rng, i, curve),
                curve);
        }

        if (dop == 1 || iterations == 1)
        {
            // One iteration, or a caller that already saturates the machine:
            // ThreadPool dispatch costs more than the work itself.
            for (int i = 0; i < iterations; i++)
            {
                ct.ThrowIfCancellationRequested();
                RunIteration(i);
            }
        }
        else
        {
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = dop,
                CancellationToken = ct
            };

            Parallel.For(0, iterations, parallelOptions, RunIteration);
        }

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: CpuBackend ran {iterations} iteration(s) x {request.TargetDurationSlices} slice(s) " +
            $"over {slices.Length} source slice(s) with DOP={dop}" +
            (seed.HasValue ? $" (seed={seed.Value})" : " (unseeded)") +
            (retainCurves ? " retaining curves" : ""));

        return Task.FromResult(accumulated.ToResult());
    }

    /// <summary>
    /// Runs one chunk of a chunked batch (ROADMAP Phase 28B). The scheduler in
    /// <see cref="CpuComputeDispatcher"/> claims chunks and calls this; on the CPU
    /// backend a chunk is a plain loop on the calling worker thread, so the
    /// scheduler — not this method — owns the parallelism.
    /// </summary>
    /// <remarks>
    /// <paramref name="startIteration"/> is the absolute batch index, and it is
    /// what seeds the per-iteration RNG (<c>new Random(seed + i)</c> with the
    /// absolute <c>i</c>) and what reaches the kernel as the iteration number.
    /// That is the whole determinism story: chunk boundaries can move without
    /// changing a single result.
    /// </remarks>
    /// <inheritdoc cref="IComputeBackend.RunIterationsAsync"/>
    public Task<MonteCarloDispatchResult> RunIterationsAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        int startIteration,
        int iterationCount,
        int? seed,
        bool retainCurves,
        CancellationToken ct)
    {
        // Fail soft at the boundary: degenerate input is an empty result, not a throw.
        if (slices == null || slices.Length == 0 || request.TargetDurationSlices <= 0 || iterationCount <= 0)
            return Task.FromResult(MonteCarloDispatchResult.Empty);

        var accumulated = new IterationAccumulator(iterationCount, request.TargetDurationSlices, retainCurves);

        for (int local = 0; local < iterationCount; local++)
        {
            // Between iterations, never mid-iteration — the same cancellation
            // granularity the whole-batch entry point documents. A cancelled
            // chunk returns a short array rather than a padded one, which is what
            // makes the scheduler's whole-chunk-or-nothing rule safe.
            ct.ThrowIfCancellationRequested();

            int i = startIteration + local;
            var curve = retainCurves ? new double[request.TargetDurationSlices] : null;

            accumulated.Store(
                local,
                _kernel.Simulate(slices, request, CreateRng(seed, i), i, curve),
                curve);
        }

        return Task.FromResult(accumulated.ToResult());
    }

    /// <summary>
    /// Splits per-iteration RNGs exactly as <c>MonteCarloEngine.Run</c> does:
    /// <c>new Random(seed + i)</c> keyed on the <em>absolute</em> iteration index,
    /// or the process-wide <see cref="Random.Shared"/> when unseeded.
    /// </summary>
    private static Random CreateRng(int? seed, int iteration)
        => seed.HasValue ? new Random(seed.Value + iteration) : Random.Shared;

    /// <summary>
    /// Collects per-iteration kernel outputs into the parallel arrays a
    /// <see cref="MonteCarloDispatchResult"/> is made of. One instance per
    /// call/chunk, written at distinct indices, so no synchronization is needed.
    /// </summary>
    private sealed class IterationAccumulator
    {
        private readonly double[] _terminalBalances;
        private readonly byte[] _outcomes;
        private readonly int[] _slicesElapsed;
        private readonly double[] _maxDrawdowns;
        private readonly double[] _bestDayRatios;
        private readonly int[] _longestLosingStreaks;
        private readonly int[] _longestFlatStretches;
        private readonly byte[] _ruinCauses;
        private readonly double[][] _equityCurves;

        public IterationAccumulator(int count, int targetDurationSlices, bool retainCurves)
        {
            _terminalBalances = new double[count];
            _outcomes = new byte[count];
            _slicesElapsed = new int[count];
            _maxDrawdowns = new double[count];
            _bestDayRatios = new double[count];
            _longestLosingStreaks = new int[count];
            _longestFlatStretches = new int[count];
            _ruinCauses = new byte[count];
            _equityCurves = retainCurves ? new double[count][] : Array.Empty<double[]>();
        }

        public void Store(int index, MonteCarloIterationOutcome outcome, double[]? curve)
        {
            _terminalBalances[index] = outcome.TerminalBalance;
            _outcomes[index] = outcome.Outcome;
            _slicesElapsed[index] = outcome.SlicesElapsed;
            _maxDrawdowns[index] = outcome.MaxDrawdownPercent;
            _bestDayRatios[index] = outcome.BestDayRatio;
            _longestLosingStreaks[index] = outcome.LongestLosingStreak;
            _longestFlatStretches[index] = outcome.LongestFlatStretch;
            _ruinCauses[index] = outcome.RuinCause;

            if (curve != null)
                _equityCurves[index] = curve;
        }

        public MonteCarloDispatchResult ToResult() => new(
            _terminalBalances,
            _outcomes,
            _slicesElapsed,
            _maxDrawdowns,
            _bestDayRatios,
            _longestLosingStreaks,
            _longestFlatStretches,
            _ruinCauses,
            _equityCurves);
    }
}
