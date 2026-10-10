using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core;
using backs2.Core.Computing.Backends;
using backs2.Core.Overview;

namespace backs2.Core.Computing;

/// <summary>
/// In-process dispatcher (ROADMAP Phase 28A) and the parent-process hybrid
/// scheduler (Phase 28B). Resolves <see cref="IComputeBackend"/> from DI, picks
/// the accelerated backend when one is available (Phase 29B), splits a Monte
/// Carlo dispatch into fixed-size chunks, and hands those chunks to available
/// workers that claim them off a shared queue until it runs dry.
///
/// <para><b>Why this lives in the parent process.</b> Phase 28B as originally
/// scoped said "in the worker", but Compute Worker Phase 3 is not scheduled and
/// does not exist, so the chunked scheduler ships here rather than parking the
/// subsystem. The dispatcher's surface is deliberately backend-agnostic and
/// IPC-shaped: migrating this loop into the worker later is wiring, not a
/// redesign.</para>
///
/// <para><b>Determinism.</b> The only detail that can silently corrupt a seeded
/// run is how per-iteration RNGs are split. The RNG for iteration <c>i</c> is
/// always keyed on the <em>absolute</em> index in <c>[0, iterations)</c> — never
/// the chunk-local index — so a chunked dispatch stays stable when
/// <see cref="McChunkSize"/> or the machine's core count changes. On the CPU
/// backend that key is <c>new Random(seed + i)</c>, bit-identical to
/// <see cref="CpuBackend.RunSimulationsAsync"/>; on the GPU backend it is a
/// Philox counter derived from the same pair. This mirrors
/// <c>MonteCarloEngine.Run</c>'s existing split semantics.</para>
///
/// <para><b>Cancellation.</b> <c>ct</c> reaches every worker through
/// <see cref="ParallelOptions.CancellationToken"/>. A cancelled run returns the
/// chunks that completed as a truncated result instead of throwing, matching the
/// Monte Carlo engine's "keep partial results" semantics.</para>
/// </summary>
public sealed class CpuComputeDispatcher : IComputeDispatcher
{
    /// <summary>
    /// Iterations per unit of work. 10k keeps one chunk comfortably inside the
    /// Windows TDR budget (the ceiling that constrains a future GPU dispatch) while
    /// still giving a 100k-iteration run enough chunks — 10 — that work-stealing
    /// can absorb a slow core.
    /// </summary>
    private const int McChunkSize = 10_000;

    private readonly ConfigManager? _config;
    private readonly IComputeBackend _cpu;
    private readonly IComputeBackend _gpu;

    /// <summary>
    /// The backend the scheduler is currently claiming chunks against. Resolved
    /// once per dispatch by <see cref="ResolveBackend"/> and cached here so the
    /// <see cref="Backend"/> property stays stable for the duration of a run.
    /// </summary>
    private IComputeBackend _backend;

    /// <summary>
    /// Tri-state answer to "is <see cref="_gpu"/> accelerated": 1 yes, 0 no, -1
    /// not asked yet. The constructor skips the question entirely in
    /// <c>Cpu</c> mode, so the first non-CPU dispatch is what asks it — and the
    /// answer is then cached, because a device does not appear or vanish between
    /// dispatches.
    /// </summary>
    private int _gpuAccelerated;

    /// <param name="backends">
    /// Every backend the process can resolve. The dispatcher picks the
    /// accelerated one when a device is available and the first registered one
    /// otherwise, so CPU-vs-GPU selection stays on this side of the DI surface
    /// instead of leaking into every caller. One registration is a valid
    /// collection: a deployment with no GPU registers only the CPU backend.
    /// </param>
    /// <param name="config">
    /// Supplies <see cref="AppSettings.MonteCarloBackend"/>. Optional, and null
    /// means <c>Auto</c>, so the dispatcher still works in a host that has no
    /// settings file (a test, a tool, the eventual Compute Worker process). The
    /// value is re-read on every dispatch rather than captured at construction,
    /// so flipping the toggle in the UI takes effect on the next run without a
    /// restart — while a run already in flight keeps the backend it started with,
    /// because the choice is resolved once per dispatch and never mid-loop.
    /// </param>
    public CpuComputeDispatcher(IEnumerable<IComputeBackend> backends, ConfigManager? config = null)
    {
        if (backends == null)
            throw new ArgumentNullException(nameof(backends));

        _config = config;

        // Probed lazily, and only when the configuration can actually use a
        // device. -1 means "not asked yet"; see ResolveBackend.
        _gpuAccelerated = -1;

        IComputeBackend? first = null;
        IComputeBackend? candidate = null;
        bool cpuOnly = IsCpuOnly();

        foreach (var backend in backends)
        {
            if (backend == null) continue;

            bool isCpuBackend = backend is CpuBackend;
            if (isCpuBackend && first == null) first = backend;

            // Never ask a deferred backend for IsAccelerated while the configured
            // mode forbids device use: the answer costs a DirectX 12 probe, and
            // MonteCarloBackend=Cpu exists precisely to avoid that cost and its
            // log line.
            if (!cpuOnly && backend.IsAccelerated)
            {
                candidate = backend;
                _gpuAccelerated = 1;
                break;
            }

            candidate ??= backend;
        }

        _cpu = first ?? new CpuBackend();
        _gpu = candidate ?? _cpu;
        _backend = _cpu;

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: dispatcher resolved {_cpu.Name}" +
            (ReferenceEquals(_gpu, _cpu) ? "" : $" + {_gpu.Name}") +
            (_gpuAccelerated == 1 ? " (accelerated backend available)" : " (device not probed yet)") +
            $" — MonteCarloBackend={ConfiguredBackend()}");
    }

    /// <summary>
    /// True when the configured mode forbids device use outright.
    /// </summary>
    private bool IsCpuOnly() =>
        string.Equals(ConfiguredBackend(), "Cpu", StringComparison.Ordinal);

    /// <summary>
    /// Gets the backend the scheduler claims chunks against. Resolved per
    /// dispatch by <see cref="ResolveBackend"/> and cached here so the
    /// <see cref="Backend"/> property stays stable for the duration of a run.
    /// </summary>
    public IComputeBackend Backend => _backend;

    /// <summary>
    /// The configured backend name, normalized, for logging and for the
    /// <c>Gpu</c> fallback warning.
    /// </summary>
    private string ConfiguredBackend() =>
        ConfigManager.NormalizeBackend(_config?.Settings.MonteCarloBackend ?? "Auto");

    /// <summary>
    /// Picks the backend for one dispatch from the <c>MonteCarloBackend</c>
    /// setting (ROADMAP Phase 29B.2):
    ///
    /// <list type="bullet">
    ///   <item><description><c>Cpu</c> — never touch the device. The accelerated
    ///   backend is not even named, so a broken driver cannot cost anything.</description></item>
    ///   <item><description><c>Gpu</c> — the accelerated backend when the probe
    ///   succeeded; otherwise one <c>[WRN]</c> and CPU. It warns instead of
    ///   throwing because "GPU requested, none present" is a degraded
    ///   configuration, not a failed run.</description></item>
    ///   <item><description><c>Auto</c> — accelerated when available, CPU
    ///   otherwise, silently. This is the pre-29B.2 behavior.</description></item>
    /// </list>
    /// </summary>
    private IComputeBackend ResolveBackend()
    {
        bool accelerated = IsGpuAccelerated();
        string configured = ConfiguredBackend();

        switch (configured)
        {
            case "Cpu":
                if (accelerated)
                {
                    LogManager.Info(LogTag.MonteCarlo,
                        "compute: MonteCarloBackend=Cpu — running all Monte Carlo work on the CPU backend, " +
                        $"device '{_gpu.Name}' not used");
                }

                return _cpu;

            case "Gpu":
                if (accelerated) return _gpu;

                // Deduplicated rather than repeated: the absence of an
                // accelerated backend is a property of the machine, not of each
                // chunk, so it is worth exactly one warning per run.
                LogManager.LogOnce.Warn(LogTag.MonteCarlo,
                    "compute: MonteCarloBackend=Gpu was requested but no accelerated backend is available " +
                    "(device probe failed or no DirectX 12 hardware) — using the CPU backend");

                return _cpu;

            default:
                return accelerated ? _gpu : _cpu;
        }
    }

    /// <summary>
    /// Caches the one-time "is the GPU backend accelerated" answer, asking the
    /// backend (and so probing the device) at most once.
    /// </summary>
    private bool IsGpuAccelerated()
    {
        if (_gpuAccelerated < 0)
            _gpuAccelerated = !ReferenceEquals(_gpu, _cpu) && _gpu.IsAccelerated ? 1 : 0;

        return _gpuAccelerated == 1;
    }

    /// <inheritdoc/>
    public async Task<MonteCarloDispatchResult> DispatchMonteCarloAsync(
        MonteCarloDispatchRequest request,
        CancellationToken ct)
    {
        if (request == null)
            return MonteCarloDispatchResult.Empty;

        if (request.Iterations <= 0 || request.Slices == null || request.Slices.Length == 0 ||
            request.Simulation.TargetDurationSlices <= 0)
        {
            LogManager.Debug(LogTag.MonteCarlo,
                "compute: MonteCarlo dispatch is degenerate — returning empty result");
            return MonteCarloDispatchResult.Empty;
        }

        var slices = request.Slices;
        int iterations = request.Iterations;
        int? seed = request.Seed;
        bool retainCurves = request.RetainCurves;

        // One resolution per dispatch, never per chunk: a toggle flipped while a
        // run is in flight must not split one batch across two backends.
        _backend = ResolveBackend();

        int chunkSize = McChunkSize;
        int chunkCount = (iterations + chunkSize - 1) / chunkSize;

        // The requested inner degree is the worker count, normalized exactly as
        // MonteCarloEngine.Run normalizes it: an outer orchestrator that already
        // saturates the machine asks for 1 so the scheduler does not oversubscribe
        // it with a second layer of threads.
        int requestedDop = request.MaxDegreeOfParallelism;
        int dop = requestedDop == 1
            ? 1
            : requestedDop > 1
                ? requestedDop
                : Environment.ProcessorCount;

        // Degenerate/cheap shapes never pay for the scheduler: one unit of work
        // claimed by one worker is exactly the sequential loop.
        int workerCount = chunkCount == 1 ? 1 : Math.Min(dop, chunkCount);

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: schedule MonteCarlo {iterations} iteration(s) as {chunkCount} chunk(s) " +
            $"of <= {chunkSize}, {workerCount} worker(s), backend={_backend.Name}" +
            $"{(seed.HasValue ? $" (seed={seed.Value})" : " (unseeded, Random.Shared)")}" +
            (retainCurves ? " retaining curves" : ""));

        return await RunChunkedAsync(
            slices, request.Simulation, iterations, seed, retainCurves,
            chunkCount, chunkSize, workerCount, ct)
            .ConfigureAwait(false);
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Backtests are not chunkable: each combination is one Roslyn execution with
    /// its own compile-cache lookup and no shared resampling, so there is nothing
    /// to split and nothing to steal. One combination is one unit of work, and the
    /// batch runs on the CPU path only — a script-backed job never enters a GPU
    /// queue, which is why this bypasses <see cref="_backend"/> entirely. Phase 29C
    /// swaps the skeleton body for the real per-combination backtest; the loop
    /// shape does not change.
    /// </remarks>
    public async Task<double[]> DispatchBacktestBatchAsync(
        int jobCount,
        CancellationToken ct)
    {
        if (jobCount <= 0)
            return Array.Empty<double>();

        // Dispatch off the caller's thread: the loop below blocks for its whole
        // duration, and the eventual Phase 28B worker-IPC proxy is genuinely async.
        await Task.Yield();

        var scores = new double[jobCount];

        LogManager.Debug(LogTag.MonteCarlo,
            $"compute: dispatch backtest batch ({jobCount} job(s), {Environment.ProcessorCount} worker(s)) " +
            "— CPU-only workload, never chunked");

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount,
            CancellationToken = ct
        };

        try
        {
            Parallel.For(0, jobCount, parallelOptions, i =>
            {
                // Skeleton only: Phase 29C supplies the per-combination backtest that
                // produces this score. The loop shape is the part that must not change
                // — non-vectorizable jobs never enter a GPU queue.
                scores[i] = i;
            });
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            LogManager.Debug(LogTag.MonteCarlo,
                $"compute: backtest batch cancelled — returning {jobCount} partial score slot(s)");
        }

        return scores;
    }

    /// <summary>
    /// The work-stealing loop: one <see cref="ConcurrentQueue{T}"/> of chunk
    /// indices, <paramref name="workerCount"/> workers, and a
    /// <see cref="ConcurrentQueue{T}.TryDequeue"/> claim on each pass. Workers exit
    /// when the queue runs dry — that is the whole "stealing" mechanism, and it
    /// costs one dequeue attempt per chunk.
    /// </summary>
    /// <remarks>
    /// Results are assembled from completed chunks only, so a cancelled dispatch
    /// is a genuinely truncated array: it never carries trailing or interior zeros
    /// that a caller could mistake for simulated balances.
    /// </remarks>
    private async Task<MonteCarloDispatchResult> RunChunkedAsync(
        TimeSlice[] slices,
        MonteCarloSimulationRequest simulation,
        int iterations,
        int? seed,
        bool retainCurves,
        int chunkCount,
        int chunkSize,
        int workerCount,
        CancellationToken ct)
    {
        var pendingChunks = new ConcurrentQueue<int>();
        for (int chunk = 0; chunk < chunkCount; chunk++)
            pendingChunks.Enqueue(chunk);

        // Each worker drops its chunk here once it holds the whole chunk. Nothing
        // is published mid-chunk, so a cancelled run's residue is always a set of
        // complete chunks.
        var completedChunks = new ConcurrentDictionary<int, MonteCarloDispatchResult>();

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = workerCount,
            CancellationToken = ct
        };

        try
        {
            await Task.Yield();

            Parallel.For(0, workerCount, parallelOptions, _ =>
            {
                while (!ct.IsCancellationRequested)
                {
                    if (!pendingChunks.TryDequeue(out int chunk))
                        return;

                    int startIteration = chunk * chunkSize;
                    int iterationCount = Math.Min(chunkSize, iterations - startIteration);

                    // Backend-agnostic on purpose: this is the call that reaches the
                    // GPU queue when the resolved backend is accelerated. No
                    // caller-side branch decides CPU vs GPU — the backend contract
                    // does.
                    var chunkResults = _backend
                        .RunIterationsAsync(
                            slices, simulation, startIteration, iterationCount,
                            seed, retainCurves, ct)
                        .GetAwaiter()
                        .GetResult();

                    // A chunk only counts once it is demonstrably whole. A backend
                    // that observed cancellation mid-chunk returns a short result,
                    // and admitting it would publish a truncated chunk under a full
                    // chunk's identity — the "truncated result" then lands on an
                    // arbitrary iteration instead of a chunk boundary.
                    if (chunkResults.Count != iterationCount)
                    {
                        LogManager.LogOnce.DebugMsg(LogTag.MonteCarlo,
                            $"compute: chunk #{chunk} abandoned mid-run " +
                            $"({chunkResults.Count}/{iterationCount} iteration(s)) on {_backend.Name}");
                        return;
                    }

                    // Publish only after the whole chunk is in hand. Deliberately
                    // silent: this line fired once per chunk — 1,841 times in the
                    // 926 KB Phase 29B log — to report a success, which the batch
                    // schedule line above already states. A chunk that does not
                    // arrive whole is still reported, just below.
                    completedChunks[chunk] = chunkResults;
                }
            });
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // Deduplicated: a cancellation reaches every worker, and each one
            // used to log the same line (20 identical lines in the Phase 29B log).
            LogManager.LogOnce.DebugMsg(LogTag.MonteCarlo,
                $"compute: MonteCarlo dispatch cancelled after {completedChunks.Count}/{chunkCount} " +
                "chunk(s) — returning the chunks that completed");
        }

        // Stitch the surviving chunks back together in batch order. Everything is
        // indexed by absolute iteration, which is exactly what the whole-batch
        // entry point would have returned for those iterations.
        var chunkIds = new int[completedChunks.Count];
        completedChunks.Keys.CopyTo(chunkIds, 0);
        Array.Sort(chunkIds);

        var ordered = new List<MonteCarloDispatchResult>(chunkIds.Length);
        foreach (int chunk in chunkIds)
            ordered.Add(completedChunks[chunk]);

        return MonteCarloDispatchResult.Concat(ordered);
    }

    /// <summary>
    /// True when <paramref name="ex"/> is a cancellation signal rather than a real
    /// failure. <see cref="Parallel.For(int, int, ParallelOptions, Action{int})"/>
    /// rethrows a cancellation as a bare <see cref="OperationCanceledException"/>,
    /// but an <see cref="AggregateException"/> is also possible depending on where
    /// the worker body observed the token — and that one must still unwrap to
    /// cancellation, which is why the check walks the inner exceptions instead of
    /// trusting the surface type.
    /// </summary>
    private static bool IsCancellation(Exception ex)
    {
        switch (ex)
        {
            case OperationCanceledException:
                return true;

            case AggregateException aggregate:
                foreach (var inner in aggregate.Flatten().InnerExceptions)
                {
                    if (!IsCancellation(inner))
                        return false;
                }

                return aggregate.InnerExceptions.Count > 0;

            default:
                return false;
        }
    }
}
