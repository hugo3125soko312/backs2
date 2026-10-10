using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;
using backs2.Core.Metrics;
using backs2.Core.Models;
using backs2.Core.Runner;

namespace backs2.Core;

public sealed class StrategyRunResult
{
    public string Path { get; init; } = "";
    public bool Success { get; init; }
    public string? Error { get; init; }
    public TimeSpan Duration { get; init; }
    public IReadOnlyList<Trade> Trades { get; init; } = Array.Empty<Trade>();
}

/// <summary>
/// Phase 30 — read-only snapshot of the per-stage backtest accumulators.
/// The caller (OptimizationRunner) converts ticks to milliseconds once, at
/// summary time, so the hot path pays one atomic add per stage and no
/// allocation. Raw ticks are carried rather than doubles because
/// Interlocked.Add has no double overload.
/// </summary>
public sealed record StrategyInstrumentationSnapshot(
    long Versions,
    long CacheHits,
    long CacheMisses,
    long CompileTicks,
    long CompileCalls,
    long CacheLookupTicks,
    long SourceReadTicks,
    long StatTicks,
    long GlobalsTicks,
    long InvokeTicks);

public class StrategyManager
{
    private readonly MetricRegistry _registry;
    private readonly SemaphoreSlim _throttle = new(1, 1);

    private readonly ConcurrentDictionary<string, CachedScript> _cache = new(StringComparer.OrdinalIgnoreCase);

    private sealed record CachedScript(DateTime LastWriteUtc, long Size, ScriptRunner<object?> Runner);

    // ------------------------------------------------------------------
    // Phase 31 — single-flight gate for the compile-on-miss path.
    //
    // One gate per cache key, so the first caller to miss a key compiles it
    // and every other caller already in flight for that same key waits and
    // then reads the published entry. Distinct keys take distinct gates and
    // still compile in parallel. The gate is dropped once its compile has
    // completed (see GetOrCreateRunnerAsync), so the map does not grow one
    // entry per key for the process lifetime.
    // ------------------------------------------------------------------

    private readonly ConcurrentDictionary<string, SemaphoreSlim> _compileGates = new(StringComparer.OrdinalIgnoreCase);

    // ------------------------------------------------------------------
    // Phase 30 — Roslyn backtest cost instrumentation (temporary
    // scaffolding, observation only).
    //
    // Six stages are timed per version with one
    // Stopwatch.GetTimestamp() pair each:
    //
    //   CacheLookup  compute the key and hit or miss the dictionary
    //   Stat         File.Exists + FileInfo (file-not-found path included)
    //   SourceRead   File.ReadAllTextAsync
    //   Compile      CSharpScript.Create + CreateDelegate, on a miss only
    //   Globals      TradeRecorder + RunnerScriptGlobals construction
    //   Invoke       runner(globals) — the script body itself
    //
    // Nothing here computes anything, skips work, or reorders a loop; the
    // timestamps are read around the existing statements. GetTimestamp is
    // safe on the parallel backtest path (per-version regions overlap, so a
    // shared Stopwatch.Restart() would not be). Accumulators are
    // thread-shared and added atomically.
    // ------------------------------------------------------------------

    private long _stageVersions;
    private long _stageCacheHits;
    private long _stageCacheMisses;

    private long _stageCompileTicks;
    private long _stageCompileCalls;
    private long _stageCacheLookupTicks;
    private long _stageSourceReadTicks;
    private long _stageStatTicks;
    private long _stageGlobalsTicks;
    private long _stageInvokeTicks;

    /// <summary>
    /// Clears every Phase 30 accumulator. Called by the caller at the start
    /// of an instrumented run, so a second run never inherits the first
    /// run's numbers (the optimizer resolves this manager as a singleton).
    /// </summary>
    public void ResetInstrumentation()
    {
        Interlocked.Exchange(ref _stageVersions, 0);
        Interlocked.Exchange(ref _stageCacheHits, 0);
        Interlocked.Exchange(ref _stageCacheMisses, 0);
        Interlocked.Exchange(ref _stageCompileTicks, 0);
        Interlocked.Exchange(ref _stageCompileCalls, 0);
        Interlocked.Exchange(ref _stageCacheLookupTicks, 0);
        Interlocked.Exchange(ref _stageSourceReadTicks, 0);
        Interlocked.Exchange(ref _stageStatTicks, 0);
        Interlocked.Exchange(ref _stageGlobalsTicks, 0);
        Interlocked.Exchange(ref _stageInvokeTicks, 0);
    }

    /// <summary>
    /// Reads the Phase 30 accumulators. `Versions` is the number of
    /// completed backtest invocations; the rest are raw Stopwatch ticks.
    /// </summary>
    public StrategyInstrumentationSnapshot GetInstrumentationSnapshot()
        => new(
            Versions: Interlocked.Read(ref _stageVersions),
            CacheHits: Interlocked.Read(ref _stageCacheHits),
            CacheMisses: Interlocked.Read(ref _stageCacheMisses),
            CompileTicks: Interlocked.Read(ref _stageCompileTicks),
            CompileCalls: Interlocked.Read(ref _stageCompileCalls),
            CacheLookupTicks: Interlocked.Read(ref _stageCacheLookupTicks),
            SourceReadTicks: Interlocked.Read(ref _stageSourceReadTicks),
            StatTicks: Interlocked.Read(ref _stageStatTicks),
            GlobalsTicks: Interlocked.Read(ref _stageGlobalsTicks),
            InvokeTicks: Interlocked.Read(ref _stageInvokeTicks));

    /// <summary>
    /// Half of a Stopwatch timestamp pair. Never throws, so the per-version
    /// try/catch cannot be perturbed by the instrumentation itself.
    /// </summary>
    private static long Stamp()
    {
        try { return Stopwatch.GetTimestamp(); }
        catch { return 0L; }
    }

    private static void AddTicks(ref long accumulator, long endTicks, long startTicks)
    {
        long delta = endTicks - startTicks;
        if (delta > 0) Interlocked.Add(ref accumulator, delta);
    }

    /// <summary>
    /// Serialises backtests through a single-thread throttle. Enabled by
    /// default for the single-run UI path. The parameter optimizer sets this
    /// to false after the --verify-strategy-parallelism check confirms the
    /// compiled <see cref="ScriptRunner{T}"/> is safe for concurrent
    /// invocation (see docs/ROADMAP_ARCHIVE.md Phase 26B.1).
    /// </summary>
    public bool SerializeBacktests { get; set; } = true;

    public StrategyManager(MetricRegistry registry)
    {
        _registry = registry;
    }

    public async Task<StrategyRunResult> RunBacktestFromFileAsync(
        string path,
        List<Kline> data,
        Dictionary<string, string>? parameters = null,
        CancellationToken ct = default)
    {
        var started = DateTime.UtcNow;

        bool tookLock = false;
        if (SerializeBacktests)
        {
            await _throttle.WaitAsync(ct).ConfigureAwait(false);
            tookLock = true;
        }

        try
        {
            Interlocked.Increment(ref _stageVersions);

            long stageStart = Stamp();

            bool exists = System.IO.File.Exists(path);
            long statStart = Stamp();
            System.IO.FileInfo? info = null;
            long size = 0;
            DateTime lastWriteUtc = default;
            if (exists)
            {
                info = new System.IO.FileInfo(path);
                lastWriteUtc = info.LastWriteTimeUtc;
                size = info.Length;
            }
            long statEnd = Stamp();
            AddTicks(ref _stageStatTicks, statEnd, stageStart);

            if (!exists)
            {
                return new StrategyRunResult
                {
                    Path = path,
                    Success = false,
                    Error = $"Strategy file not found: {path}",
                    Duration = DateTime.UtcNow - started
                };
            }

            long readStart = Stamp();
            var source = await System.IO.File.ReadAllTextAsync(path, ct).ConfigureAwait(false);
            AddTicks(ref _stageSourceReadTicks, Stamp(), readStart);

            var runner = await GetOrCreateRunnerAsync(path, lastWriteUtc, size, source, ct)
                .ConfigureAwait(false);

            long globalsStart = Stamp();
            var recorder = new TradeRecorder(data);

            var globals = new RunnerScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = msg => LogManager.Info(LogTag.Strategy, msg),
                Trades = recorder,
                Params = parameters ?? new Dictionary<string, string>()
            };
            AddTicks(ref _stageGlobalsTicks, Stamp(), globalsStart);

            long invokeStart = Stamp();
            await runner(globals).ConfigureAwait(false);
            AddTicks(ref _stageInvokeTicks, Stamp(), invokeStart);

            LogManager.Info(LogTag.Strategy, $"executed {System.IO.Path.GetFileName(path)}");

            return new StrategyRunResult
            {
                Path = path,
                Success = true,
                Duration = DateTime.UtcNow - started,
                Trades = recorder.Closed
            };
        }
        catch (Exception ex)
        {
            LogManager.Error(LogTag.Strategy, $"failed {System.IO.Path.GetFileName(path)}", ex);
            return new StrategyRunResult
            {
                Path = path,
                Success = false,
                Error = ex.Message,
                Duration = DateTime.UtcNow - started
            };
        }
        finally
        {
            if (tookLock) _throttle.Release();
        }
    }

    public async Task RunBacktestAsync(string script, List<Kline> data)
    {
        bool tookLock = false;
        if (SerializeBacktests)
        {
            await _throttle.WaitAsync().ConfigureAwait(false);
            tookLock = true;
        }

        try
        {
            var recorder = new TradeRecorder(data);

            var globals = new RunnerScriptGlobals
            {
                Engine = _registry,
                Data = data,
                Log = msg => LogManager.Info(LogTag.Strategy, msg),
                Trades = recorder,
                Params = new Dictionary<string, string>()
            };

            var options = ScriptOptions.Default
                .AddReferences(typeof(IMetric).Assembly)
                .AddImports("backs2.Core.Metrics", "backs2.Core.Models", "backs2.Core.Runner");

            await CSharpScript.EvaluateAsync(script, options, globals).ConfigureAwait(false);
        }
        finally
        {
            if (tookLock) _throttle.Release();
        }
    }

    /// <summary>
    /// Phase 31 — single-flight compile-on-miss.
    ///
    /// The fast path is unchanged: one dictionary lookup, and a lookup that
    /// finds the entry current for the caller's (path, last write, size)
    /// returns without touching a gate. On a miss the compile is serialised
    /// per cache key by <see cref="_compileGates"/> and the cache is
    /// re-checked inside the gate, so N callers that miss the same key
    /// concurrently produce one compile rather than N. Callers that arrive
    /// while that compile runs observe the published entry and do not
    /// compile at all -- which is why the cold pass still reports 20 misses
    /// (one per first-wave lookup) but 1 compile.
    ///
    /// The gate is async: a waiting caller yields its thread instead of
    /// holding one of the 20 backtest workers while another compiles.
    ///
    /// Compile failure semantics are unchanged: the entry is published only
    /// after <c>CreateDelegate</c> returns, so a failed compile caches
    /// nothing and the next caller retries (as it did before Phase 31),
    /// rather than inheriting a cached exception.
    /// </summary>
    private async Task<ScriptRunner<object?>> GetOrCreateRunnerAsync(
        string path,
        DateTime lastWriteUtc,
        long size,
        string source,
        CancellationToken ct)
    {
        long lookupStart = Stamp();
        bool hit = _cache.TryGetValue(path, out var existing)
            && existing.LastWriteUtc == lastWriteUtc
            && existing.Size == size;
        AddTicks(ref _stageCacheLookupTicks, Stamp(), lookupStart);

        if (hit)
        {
            Interlocked.Increment(ref _stageCacheHits);
            LogManager.Debug(LogTag.Strategy, $"compile cache hit: {System.IO.Path.GetFileName(path)}");
            return existing!.Runner;
        }

        Interlocked.Increment(ref _stageCacheMisses);
        LogManager.Debug(LogTag.Strategy, $"compile cache miss: {System.IO.Path.GetFileName(path)}");

        var gate = _compileGates.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Double-checked: a caller that lost the race to this key finds
            // the winner's entry here and skips the compile entirely.
            if (_cache.TryGetValue(path, out var published)
                && published.LastWriteUtc == lastWriteUtc
                && published.Size == size)
            {
                LogManager.Debug(LogTag.Strategy,
                    $"compile cache hit (single-flight): {System.IO.Path.GetFileName(path)}");
                return published.Runner;
            }

            LogManager.Debug(LogTag.Strategy,
                $"single-flight compile: {System.IO.Path.GetFileName(path)}");

            long compileStart = Stamp();

            var options = ScriptOptions.Default
                .AddReferences(typeof(IMetric).Assembly)
                .AddImports("backs2.Core.Metrics", "backs2.Core.Models", "backs2.Core.Runner");

            var script = CSharpScript.Create<object?>(
                source,
                options,
                typeof(RunnerScriptGlobals));

            var runner = script.CreateDelegate();

            Interlocked.Increment(ref _stageCompileCalls);
            AddTicks(ref _stageCompileTicks, Stamp(), compileStart);

            _cache[path] = new CachedScript(lastWriteUtc, size, runner);
            return runner;
        }
        finally
        {
            // Release, then drop the gate. On the success path the publish
            // above happens-before both, so a caller that took this gate
            // before the removal and one that takes a fresh gate after it
            // both re-check the cache and see the entry; removing the gate
            // cannot add a compile. On the exception path nothing was
            // published and the gate is dropped anyway, so a failed compile
            // leaves nothing held and the next caller retries immediately.
            gate.Release();
            _compileGates.TryRemove(new KeyValuePair<string, SemaphoreSlim>(path, gate));
        }
    }
}
