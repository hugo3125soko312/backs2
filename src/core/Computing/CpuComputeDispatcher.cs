using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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

    /// <summary>
    /// Total iterations below which a Monte Carlo batch is not sent to the device.
    /// Measured crossover on an RTX 4060 Laptop: the GPU loses 1.5x at 24k total
    /// iterations (the optimizer coarse pass), reaches parity near 500k, and wins
    /// above that. Set with margin so the device is never tried where it loses.
    /// </summary>
    private const long GpuBatchMinIterations = 400_000L;

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

    /// <inheritdoc/>
    /// <remarks>
    /// One flat pool over the job indices, each job calling the caller-supplied
    /// body. No device involvement: a Roslyn backtest is not vectorizable and has
    /// no resampling to chunk.
    /// </remarks>
    public async Task<double[]> DispatchBacktestBatchAsync(
        int jobCount,
        Func<int, CancellationToken, Task<double>> work,
        int maxDegreeOfParallelism,
        CancellationToken ct)
    {
        if (jobCount <= 0 || work == null) return Array.Empty<double>();

        int dop = maxDegreeOfParallelism == 1
            ? 1
            : maxDegreeOfParallelism > 1 ? maxDegreeOfParallelism : Environment.ProcessorCount;

        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Min(dop, jobCount),
            CancellationToken = ct
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, jobCount), options, async (i, token) =>
        {
            _ = await work(i, token).ConfigureAwait(false);
        }).ConfigureAwait(false);

        return Array.Empty<double>();
    }

    /// <summary>
    /// Attempts the whole batch as one device dispatch (ROADMAP Phase 29B.1).
    /// Returns null — meaning "use the CPU path" — when the batch is not
    /// uniform, retains curves, or the device declined. A null return is not an
    /// error: the caller simply falls through to the flat CPU pool.
    /// </summary>
    private MonteCarloDispatchResult[]? TryGpuBatch(
        GpuBackend gpu,
        IReadOnlyList<MonteCarloDispatchRequest> requests,
        CancellationToken ct)
    {
        int count = requests.Count;
        var first = requests[0];
        if (first == null) return null;
        if (first.RetainCurves) return null;
        if (first.Iterations <= 0) return null;

        // Uniformity gate: one simulation shape, one iteration count. Anything
        // else is a caller bug the per-version path still handles correctly.
        for (int i = 1; i < count; i++)
        {
            var r = requests[i];
            if (r == null) return null;
            if (r.RetainCurves) return null;
            if (r.Iterations != first.Iterations) return null;
            if (!r.Simulation.Equals(first.Simulation)) return null;
        }

        if (ct.IsCancellationRequested) return null;

        var slices = new TimeSlice[count][];
        var seeds = new int[count];
        for (int i = 0; i < count; i++)
        {
            slices[i] = requests[i].Slices ?? Array.Empty<TimeSlice>();
            seeds[i] = requests[i].Seed ?? 0;
        }

        // Measured 2026-10-25 (--mc-bench): the GPU batch pays a fixed cost per
        // dispatch (slice mirror, upload, readback, nine output buffers) that only
        // amortizes above roughly 250k total iterations. The optimizer coarse pass
        // is ~24k, where the batched CPU pool wins by ~1.5x. Below the threshold the
        // device is not even tried.
        long totalIterations = (long)count * first.Iterations;
        if (totalIterations < GpuBatchMinIterations) return null;

        var flat = gpu.RunBatchAsync(slices, first.Simulation, first.Iterations, seeds, false);
        if (flat == null) return null;

        int per = first.Iterations;
        var results = new MonteCarloDispatchResult[count];
        for (int i = 0; i < count; i++)
        {
            int from = i * per;
            results[i] = new MonteCarloDispatchResult(
                Slice(flat.TerminalBalances, from, per),
                Slice(flat.Outcomes, from, per),
                Slice(flat.SlicesElapsed, from, per),
                Slice(flat.MaxDrawdowns, from, per),
                Slice(flat.BestDayRatios, from, per),
                Slice(flat.LongestLosingStreaks, from, per),
                Slice(flat.LongestFlatStretches, from, per),
                Slice(flat.RuinCauses, from, per),
                Array.Empty<double[]>());
        }
        LogManager.Debug(LogTag.MonteCarlo,
            "compute: GPU batch " + count + " version(s) x " + per + " iteration(s) in one dispatch");
        return results;
    }

    private static T[] Slice<T>(T[] source, int from, int length)
    {
        if (source == null || from < 0 || from + length > source.Length) return Array.Empty<T>();
        var dst = new T[length];
        Array.Copy(source, from, dst, 0, length);
        return dst;
    }

    /// <inheritdoc/>
    public async Task<MonteCarloDispatchResult[]> DispatchMonteCarloBatchAsync(
        IReadOnlyList<MonteCarloDispatchRequest> requests,
        CancellationToken ct)
    {
        if (requests == null || requests.Count == 0)
            return Array.Empty<MonteCarloDispatchResult>();

        int count = requests.Count;
        var results = new MonteCarloDispatchResult[count];

        // Batch of one is the single-request path, unchanged.
        if (count == 1)
        {
            results[0] = await DispatchMonteCarloAsync(requests[0], ct).ConfigureAwait(false);
            return results;
        }

        // Resolve once for the whole batch, exactly as the single-request path
        // resolves once per dispatch: one batch never straddles two backends.
        _backend = ResolveBackend();

        // GPU batch path: one device dispatch for the whole pass. It is only
        // usable when every version shares the same simulation parameters (the
        // optimizer coarse pass always does) and no curves are retained.
        if (_backend is GpuBackend gpu)
        {
            var gpuResult = TryGpuBatch(gpu, requests, ct);
            if (gpuResult != null) return gpuResult;
        }

        // Longest-first ordering so the tail is short. Each version carries a
        // different slice count, so an unordered queue lets a heavy version start
        // last and leave the pool half idle.
        var order = new int[count];
        for (int i = 0; i < count; i++) order[i] = i;
        Array.Sort(order, (a, b) =>
        {
            int la = requests[a]?.Slices?.Length ?? 0;
            int lb = requests[b]?.Slices?.Length ?? 0;
            int cmp = lb.CompareTo(la);
            return cmp != 0 ? cmp : a.CompareTo(b);
        });

        var queue = new ConcurrentQueue<int>();
        foreach (int idx in order) queue.Enqueue(idx);

        int workers = Math.Min(Environment.ProcessorCount, count);
        var options = new ParallelOptions
        {
            CancellationToken = ct,
            MaxDegreeOfParallelism = workers
        };

        LogManager.Debug(LogTag.MonteCarlo,
            "compute: batch MonteCarlo " + count + " version(s), " + workers +
            " worker(s), backend=" + _backend.Name);

        try
        {
            Parallel.For(0, workers, options, _ =>
            {
                while (queue.TryDequeue(out int idx))
                {
                    if (ct.IsCancellationRequested) return;
                    var req = requests[idx];
                    if (req == null) { results[idx] = MonteCarloDispatchResult.Empty; continue; }

                    // MaxDegreeOfParallelism = 1: the flat pool above owns the
                    // parallelism, so a nested scheduler would oversubscribe.
                    var inner = req with { MaxDegreeOfParallelism = 1 };
                    results[idx] = DispatchMonteCarloAsync(inner, ct)
                        .ConfigureAwait(false).GetAwaiter().GetResult();
                }
            });
        }
        catch (Exception ex) when (IsCancellation(ex))
        {
            // Completed items keep their result; the rest stay null and are
            // normalized to Empty below, so the array length never shifts.
        }

        for (int i = 0; i < count; i++)
            results[i] ??= MonteCarloDispatchResult.Empty;

        return results;
    }
}
