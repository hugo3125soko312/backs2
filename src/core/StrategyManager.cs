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
///
/// Phase 33 — <see cref="InvokePlumbingTicks"/> and
/// <see cref="InvokeCallTicks"/> subdivide <see cref="InvokeTicks"/> at the
/// only seam the scripting API exposes from outside: the arguments handed to
/// the delegate versus the delegate call itself.
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
    long InvokeTicks,
    long InvokePlumbingTicks,
    long InvokeCallTicks);

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
    // Phase 33 subdivides Invoke into InvokePlumbing (argument construction)
    // and InvokeCall (the delegate call and everything Roslyn does inside
    // it); see the Phase 33 block below.
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

    // ------------------------------------------------------------------
    // Phase 33 — subdivision of the Phase 30 Invoke stage.
    //
    // Invoke was measured as the whole `runner(globals)` region, so every
    // Roslyn scripting-API cost sat inside one number. From outside the
    // scripting API there is exactly one seam to cut it at: the handoff
    // between "the runner is in hand and its arguments are built" and "the
    // delegate is called". InvokePlumbing covers the former (globals object
    // construction, Params handoff, argument preparation); InvokeCall covers
    // the latter (everything Roslyn does inside the delegate).
    //
    //   InvokePlumbing  arg construction: TradeRecorder + RunnerScriptGlobals
    //                   object initialiser + the argument tuple for the call
    //   InvokeCall      the delegate invocation itself, i.e. every Roslyn
    //                   internal: session/execution-state construction,
    //                   dynamic globals dispatch, method resolution and
    //                   argument marshalling. Not separable further from
    //                   outside the API (see the comment at the call site).
    //
    // Both are read with the same one-Stopwatch.GetTimestamp()-pair-per-
    // version budget as the Phase 30 stages, and both are additive with
    // Invoke: Plumbing + Call ~= Invoke. The two extra reads are the entire
    // cost this phase adds to the hot path.
    // ------------------------------------------------------------------

    private long _stageInvokePlumbingTicks;
    private long _stageInvokeCallTicks;

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
        Interlocked.Exchange(ref _stageInvokePlumbingTicks, 0);
        Interlocked.Exchange(ref _stageInvokeCallTicks, 0);
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
            InvokeTicks: Interlocked.Read(ref _stageInvokeTicks),
            InvokePlumbingTicks: Interlocked.Read(ref _stageInvokePlumbingTicks),
            InvokeCallTicks: Interlocked.Read(ref _stageInvokeCallTicks));

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

            // ------------------------------------------------------------------
            // Phase 33 — the invoke path is deliberately untouched. The two
            // timestamp pairs below bracket the statements that were already
            // here; nothing is hoisted, cached, wrapped or reordered, because
            // the point of the measurement is what the current path costs.
            //
            // `invokeStart` still spans everything the Phase 30 [30] line
            // reports as "invoke", so the Phase 30 numbers stay directly
            // comparable. `plumbingStart` subdivides that same span at the
            // delegate boundary; the plumbing region is the Phase 30 globals
            // region plus the argument preparation for the call.
            // ------------------------------------------------------------------

            long invokeStart = Stamp();
            long plumbingStart = invokeStart;

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

            // The plumbing region ends at the last statement whose result is
            // an argument to the delegate call. Everything from here to the
            // matching stamp belongs to Roslyn: ScriptRunner's own invocation
            // performs the execution-state construction, the globals handoff
            // and the compiled-submission call internally, and none of that is
            // observable from outside the scripting API. The `await` and
            // `.ConfigureAwait(false)` are part of the call region because the
            // task-await continuation is what ScriptRunner returns.
            //
            // The one thing the split cannot price is the delegate call-site
            // dispatch itself (loading the two arguments and entering the
            // delegate) between the plumbing stamp and the callee's first
            // instruction; it is one delegate entry against a per-call span of
            // milliseconds, so it is not worth a third timestamp pair, but
            // Plumbing + Call is therefore a lower bound on Invoke rather than
            // an exact sum.
            long callStart = Stamp();
            AddTicks(ref _stageInvokePlumbingTicks, callStart, plumbingStart);

            await runner(globals).ConfigureAwait(false);
            long callEnd = Stamp();

            AddTicks(ref _stageInvokeCallTicks, callEnd, callStart);
            AddTicks(ref _stageInvokeTicks, callEnd, invokeStart);

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
            LogManager.LogOnce.DebugMsg(LogTag.Strategy, $"compile cache hit: {System.IO.Path.GetFileName(path)}");
            return existing!.Runner;
        }

        Interlocked.Increment(ref _stageCacheMisses);
        LogManager.LogOnce.DebugMsg(LogTag.Strategy, $"compile cache miss: {System.IO.Path.GetFileName(path)}");

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
