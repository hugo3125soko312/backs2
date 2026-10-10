using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Models;
using backs2.Core.Overview;
using backs2.Core.Runner;

namespace backs2.Core.Optimizer;

public sealed record OptimizationRunConfig(
    int MaxCombinations,
    int CoarseIterations,
    int RefineIterations,
    int RefineTopN,
    int MeanBlockSize,
    double StartingCapital,
    string DrawdownType,
    double MaxLossPercent,
    double MaxDailyLossPercent,
    double ProfitTargetPercent,
    double BestDayRulePercent,
    int TradingPeriodDays,
    double GpuFactor = 1.0);

public sealed class OptimizationRunner
{
    private const int EdgeMinTrades = 30;
    private const int EdgeNullSeed = 42;
    private const int RepresentativeCurveLength = 1000;

    private readonly StrategyManager _strat;
    private readonly MonteCarloEngine _mc;
    private readonly OptimizationStore _store;

    // ------------------------------------------------------------------
    // Phase 29A — stage instrumentation (temporary scaffolding).
    //
    // Per-stage accumulated timestamps, in raw Stopwatch ticks. The
    // instrumentation is observation only: nothing here changes a
    // computation, skips work, or reorders a loop. Ticks are converted to
    // milliseconds once, in the closing summary, so the hot path pays one
    // Interlocked.Add per instrumented region and no allocation.
    //
    // Timestamps come from Stopwatch.GetTimestamp() +
    // Stopwatch.GetElapsedTime(start, end), matching the [startup] marker
    // style in App.axaml.cs. Unlike a shared Stopwatch.Restart(), that is
    // safe on the parallel backtest path, where per-version regions run
    // concurrently and their elapsed ranges overlap.
    //
    // Accumulators are per-instance and OptimizationRunner is a DI
    // singleton, so RunAsync resets them before every run. The UI runs one
    // Optimize pass at a time (IsBusy guards it), which is what makes the
    // reset safe.
    // ------------------------------------------------------------------

    private long _callsSliceBuild;
    private long _callsCoarseMc;
    private long _callsRefineMc;
    private long _callsNullGenerate;

    private long _stageBacktestTicks;
    private long _stageCompileLookupTicks;
    private long _stageSliceBuildTicks;
    private long _stageMcCallTicks;
    private long _stageNullGenerateTicks;

    private long _stageCompileLookupVersionTicks;
    private long _stageCoarseVersionTicks;
    private long _stageRefineVersionTicks;

    // Converted from accumulated ticks once, at summary time.
    private double _msBacktest;
    private double _msDiskIo;
    private double _msSliceBuild;
    private double _msMcCall;
    private double _msCoarseVersion;
    private double _msRefineVersion;
    private double _msNullGenerate;

    // ------------------------------------------------------------------
    // Phase 30 — Roslyn backtest cost split (temporary scaffolding).
    //
    // StrategyManager accumulates the stage timings; this class owns the
    // reader side only. Converted from ticks once, at summary time.
    // ------------------------------------------------------------------

    private double _msCompile;
    private double _msCacheLookup;
    private double _msSourceRead;
    private double _msStat;
    private double _msGlobals;
    private double _msInvoke;

    // ------------------------------------------------------------------
    // Phase 33 — subdivision of the Phase 30 Invoke stage.
    //
    // StrategyManager brackets the delegate call on both sides; this class
    // does the arithmetic. `_msInvokePlumbing` is the argument-construction
    // half, `_msInvokeCall` is the delegate call half and therefore every
    // Roslyn internal that the scripting API does not expose. Converted from
    // ticks once, at summary time, like every other stage here.
    // ------------------------------------------------------------------

    private double _msInvokePlumbing;
    private double _msInvokeCall;

    private long _compileCalls;
    private long _cacheHits;
    private long _cacheMisses;
    private long _instrumentedVersions;

    public OptimizationRunner(StrategyManager strat, MonteCarloEngine mc, OptimizationStore store)
    {
        _strat = strat;
        _mc = mc;
        _store = store;
    }

    public async Task<OptimizationBatch> RunAsync(
        string strategyPath,
        string strategyName,
        string symbol,
        string timeframe,
        List<Kline> data,
        IReadOnlyList<GridParameter> grid,
        OptimizationRunConfig config,
        IProgress<OptimizationProgress>? progress,
        CancellationToken ct = default)
    {
        if (data == null || data.Count < 2)
            throw new ArgumentException("Optimization needs a non-empty data series.", nameof(data));

        var wall = Stopwatch.StartNew();
        ResetStageTimers();

        // Setup runs on this thread, so a plain timestamp pair is enough.
        long setupStart = Stopwatch.GetTimestamp();
        double setupMs = 0.0;
        double backtestMs = 0.0;
        double coarseMs = 0.0;
        double refineMs = 0.0;
        double saveMs = 0.0;
        int survivorCount = 0;

        var batchId = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        double dataSpanYears = ComputeDataSpanYears(data);

        var eta = new EtaEstimator();

        progress?.Report(new OptimizationProgress("Grid", 0, 1, "Generating parameter grid..."));
        var gridResult = ParameterGridBuilder.Build(grid, config.MaxCombinations);

        string sampleNote = gridResult.WasSampled
            ? $" (sampled {gridResult.Combinations.Count} of {gridResult.TotalGenerated})"
            : "";
        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: {gridResult.Combinations.Count} combination(s){sampleNote} " +
            $"for {strategyName} on {symbol}@{timeframe}");

        progress?.Report(new OptimizationProgress(
            "Grid", 1, 1,
            $"{gridResult.Combinations.Count} combination(s){sampleNote}"));

        int comboCount = gridResult.Combinations.Count;

        // ------------------------------------------------------------------
        // Topology precompute. Outer degree = min(items, cores); inner degree
        // = cores / outer. Same values used to gate curve retention later.
        // ------------------------------------------------------------------

        int backtestOuter = Math.Min(comboCount, Environment.ProcessorCount);
        int coarseOuter   = Math.Min(comboCount, Environment.ProcessorCount);
        int refineOuter   = Math.Min(config.RefineTopN, Environment.ProcessorCount);

        int coarseInner   = Math.Max(1, Environment.ProcessorCount / Math.Max(1, coarseOuter));
        int refineInner   = Math.Max(1, Environment.ProcessorCount / Math.Max(1, refineOuter));

        LogManager.Debug(LogTag.Runner,
            $"optimization {batchId}: topology backtest={backtestOuter}, " +
            $"coarse={coarseOuter}(inner {coarseInner}), refine={refineOuter}(inner {refineInner})");

        // Freeze the estimate. Counts down from here; only ever decreases.
        eta.Start(
            combinationCount: comboCount,
            refineTopN: config.RefineTopN,
            coarseIterations: config.CoarseIterations,
            refineIterations: config.RefineIterations);

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: ETA estimate {eta.TotalEstimateSeconds:F1}s - {eta.Breakdown}");

        var versions = new OptimizationVersion[comboCount];
        var tradesByVersion = new IReadOnlyList<Trade>[comboCount];

        bool savedSerialize = _strat.SerializeBacktests;
        _strat.SerializeBacktests = false;

        setupMs = Stopwatch.GetElapsedTime(setupStart, Stopwatch.GetTimestamp()).TotalMilliseconds;

        // The whole run body sits inside one try/finally so the Phase 29A
        // summary is emitted on the cancel path as well as the success path.
        // `savedSerialize` is captured before the try because the inner
        // finally that restores it must still run when the summary throws.
        try
        {
        long backtestStart = Stopwatch.GetTimestamp();
        try
        {
            int completedBacktests = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = backtestOuter,
                CancellationToken = ct
            };

            await Parallel.ForEachAsync(
                Enumerable.Range(0, comboCount),
                parallelOptions,
                async (i, token) =>
                {
                    var paramSet = gridResult.Combinations[i];
                    var paramDict = new Dictionary<string, string>(paramSet.Length);
                    foreach (var p in paramSet)
                        paramDict[p.Name] = p.Value;

                    long versionStart = Stopwatch.GetTimestamp();
                    var result = await _strat.RunBacktestFromFileAsync(strategyPath, data, paramDict, token)
                        .ConfigureAwait(false);
                    long versionEnd = Stopwatch.GetTimestamp();
                    double versionMs = TicksToMs(versionEnd - versionStart);
                    Interlocked.Add(ref _stageBacktestTicks, versionEnd - versionStart);

                    // RunBacktestFromFileAsync does the per-version disk read
                    // (File.ReadAllTextAsync) and then the compile-cache path
                    // in StrategyManager. StrategyManager is out of scope for
                    // this phase, so the compile-cache lookup is measured
                    // rather than wrapped: total call time minus script
                    // execution (StrategyRunResult.Duration) leaves everything
                    // StrategyManager does around the script — the file
                    // existence check, the FileInfo stat, the compile-cache
                    // lookup in GetOrCreateRunner, and the source read. That
                    // residual is one number because the lookup and the read
                    // are not separable from outside StrategyManager; it is a
                    // derived span, not a directly wrapped one.
                    double scriptMs = result.Duration.TotalMilliseconds;
                    double lookupMs = versionMs > scriptMs ? versionMs - scriptMs : 0.0;
                    Interlocked.Add(ref _stageCompileLookupTicks,
                        (long)Math.Round(lookupMs * Stopwatch.Frequency / 1000.0));
                    Interlocked.Add(ref _stageCompileLookupVersionTicks, 1);

                    tradesByVersion[i] = result.Trades;

                    double netPnl = 0.0;
                    for (int t = 0; t < result.Trades.Count; t++)
                        netPnl += result.Trades[t].PnL;

                    versions[i] = new OptimizationVersion(
                        Index: i,
                        Parameters: paramSet,
                        BacktestSuccess: result.Success,
                        BacktestError: result.Error,
                        BacktestDurationMs: (long)result.Duration.TotalMilliseconds,
                        TradeCount: result.Trades.Count,
                        NetPnl: netPnl,
                        McRan: false,
                        Mc: null,
                        TerminalBalances: Array.Empty<double>(),
                        RepresentativeCurve: Array.Empty<double>(),
                        CurvesRetained: false);

                    int done = Interlocked.Increment(ref completedBacktests);

                    progress?.Report(new OptimizationProgress(
                        "Backtest", done, comboCount,
                        $"Backtest {done}/{comboCount}",
                        eta.Remaining()));
                }).ConfigureAwait(false);
        }
        finally
        {
            _strat.SerializeBacktests = savedSerialize;
        }

        backtestMs = TicksToMs(Stopwatch.GetTimestamp() - backtestStart);

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: backtests complete in {wall.ElapsedMilliseconds} ms");

        int targetSlices = ComputeTargetSlices(data, config.TradingPeriodDays);

        // ------------------------------------------------------------------
        // Phase 2 - coarse MC, outer-parallel with adaptive inner degree.
        // Coarse curves are never consumed, so retainCurves is false here.
        // ------------------------------------------------------------------

        long coarseStart = Stopwatch.GetTimestamp();

        int coarseDone = 0;
        var coarseOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = coarseOuter,
            CancellationToken = ct
        };

        Parallel.For(0, comboCount, coarseOptions, i =>
        {
            ct.ThrowIfCancellationRequested();

            long versionStart = Stopwatch.GetTimestamp();

            var v = versions[i];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();

            if (!v.BacktestSuccess || trades.Count < 2)
            {
                int d = Interlocked.Increment(ref coarseDone);
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", d, comboCount,
                    $"Coarse MC {d}/{comboCount} (skipped)",
                    eta.Remaining()));
                Interlocked.Add(ref _stageCoarseVersionTicks, Stopwatch.GetTimestamp() - versionStart);
                return;
            }

            long sliceStart = Stopwatch.GetTimestamp();
            var slices = TimeSliceBuilder.Build(trades, data);
            StageCall(ref _callsSliceBuild);
            Interlocked.Add(ref _stageSliceBuildTicks, Stopwatch.GetTimestamp() - sliceStart);

            if (slices.Length == 0)
            {
                int d = Interlocked.Increment(ref coarseDone);
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", d, comboCount,
                    $"Coarse MC {d}/{comboCount} (no slices)",
                    eta.Remaining()));
                Interlocked.Add(ref _stageCoarseVersionTicks, Stopwatch.GetTimestamp() - versionStart);
                return;
            }

            long mcStart = Stopwatch.GetTimestamp();
            var mcResult = _mc.Run(
                slices,
                targetSlices,
                config.StartingCapital,
                config.DrawdownType,
                config.MaxDailyLossPercent,
                config.MaxLossPercent,
                config.ProfitTargetPercent,
                iterations: config.CoarseIterations,
                expectedBlockSize: config.MeanBlockSize,
                bestDayRulePercent: config.BestDayRulePercent,
                seed: null,
                progress: null,
                cancellationToken: ct,
                maxDegreeOfParallelism: coarseInner,
                retainCurves: false);
            StageCall(ref _callsCoarseMc);
            Interlocked.Add(ref _stageMcCallTicks, Stopwatch.GetTimestamp() - mcStart);

            versions[i] = v with
            {
                McRan = true,
                Mc = BuildMcSummary(mcResult, edgePercent: double.NaN, nullMean: double.NaN, nullIterations: 0),
                TerminalBalances = SanitizeArray(mcResult.TerminalBalances),
                RepresentativeCurve = Array.Empty<double>(),
                CurvesRetained = false
            };

            int done = Interlocked.Increment(ref coarseDone);

            progress?.Report(new OptimizationProgress(
                "Coarse MC", done, comboCount,
                $"Coarse MC {done}/{comboCount}",
                eta.Remaining()));

            Interlocked.Add(ref _stageCoarseVersionTicks, Stopwatch.GetTimestamp() - versionStart);
        });

        coarseMs = TicksToMs(Stopwatch.GetTimestamp() - coarseStart);
        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: coarse MC complete at {wall.ElapsedMilliseconds} ms");

        var survivors = versions
            .Select((v, i) => (Version: v, Index: i))
            .Where(t => t.Version.McRan && t.Version.Mc != null)
            .OrderByDescending(t => t.Version.Mc!.PFunded)
            .ThenByDescending(t => t.Version.Mc!.TerminalBalanceMedian)
            .Take(config.RefineTopN)
            .ToList();
        survivorCount = survivors.Count;

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: refining top {survivors.Count} survivor(s) at {config.RefineIterations} iterations");

        // ------------------------------------------------------------------
        // Phase 3 - refine MC, outer-parallel with adaptive inner degree.
        // Curve retention gated on the actual outer degree, not the CPU count.
        // ------------------------------------------------------------------

        long refineCellsPerCall = (long)config.RefineIterations * targetSlices;
        bool refineRetainCurves = refineOuter <= 1
            || refineCellsPerCall * refineOuter <= MonteCarloEngine.MaxCurveCells;

        if (!refineRetainCurves)
        {
            LogManager.Warn(LogTag.Runner,
                $"optimization {batchId}: refine curves disabled - " +
                $"{refineCellsPerCall * refineOuter:N0} cells across {refineOuter} worker(s) " +
                $"would exceed MaxCurveCells ({MonteCarloEngine.MaxCurveCells:N0})");
        }

        long refineStart = Stopwatch.GetTimestamp();

        int refineDone = 0;
        var refineOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = refineOuter,
            CancellationToken = ct
        };

        Parallel.For(0, survivors.Count, refineOptions, j =>
        {
            ct.ThrowIfCancellationRequested();

            long versionStart = Stopwatch.GetTimestamp();

            var (v, i) = survivors[j];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();

            long sliceStart = Stopwatch.GetTimestamp();
            var slices = TimeSliceBuilder.Build(trades, data);
            StageCall(ref _callsSliceBuild);
            Interlocked.Add(ref _stageSliceBuildTicks, Stopwatch.GetTimestamp() - sliceStart);

            if (slices.Length == 0)
            {
                Interlocked.Increment(ref refineDone);
                Interlocked.Add(ref _stageRefineVersionTicks, Stopwatch.GetTimestamp() - versionStart);
                return;
            }

            long mcStart = Stopwatch.GetTimestamp();
            var mcResult = _mc.Run(
                slices,
                targetSlices,
                config.StartingCapital,
                config.DrawdownType,
                config.MaxDailyLossPercent,
                config.MaxLossPercent,
                config.ProfitTargetPercent,
                iterations: config.RefineIterations,
                expectedBlockSize: config.MeanBlockSize,
                bestDayRulePercent: config.BestDayRulePercent,
                seed: null,
                progress: null,
                cancellationToken: ct,
                maxDegreeOfParallelism: refineInner,
                retainCurves: refineRetainCurves);
            StageCall(ref _callsRefineMc);
            Interlocked.Add(ref _stageMcCallTicks, Stopwatch.GetTimestamp() - mcStart);

            double edgePct = double.NaN;
            double nullMean = double.NaN;
            int nullIters = 0;

            if (trades.Count >= EdgeMinTrades)
            {
                long nullStart = Stopwatch.GetTimestamp();
                var nullTrades = NullModelGenerator.GenerateRandomSignalTrades(trades, EdgeNullSeed);
                StageCall(ref _callsNullGenerate);
                Interlocked.Add(ref _stageNullGenerateTicks, Stopwatch.GetTimestamp() - nullStart);

                long nullSliceStart = Stopwatch.GetTimestamp();
                var nullSlices = TimeSliceBuilder.Build(nullTrades, data);
                StageCall(ref _callsSliceBuild);
                Interlocked.Add(ref _stageSliceBuildTicks, Stopwatch.GetTimestamp() - nullSliceStart);

                long nullMcStart = Stopwatch.GetTimestamp();
                var nullResult = _mc.Run(
                    nullSlices,
                    targetSlices,
                    config.StartingCapital,
                    config.DrawdownType,
                    config.MaxDailyLossPercent,
                    config.MaxLossPercent,
                    config.ProfitTargetPercent,
                    iterations: config.RefineIterations,
                    expectedBlockSize: config.MeanBlockSize,
                    bestDayRulePercent: config.BestDayRulePercent,
                    seed: EdgeNullSeed,
                    progress: null,
                    cancellationToken: ct,
                    maxDegreeOfParallelism: refineInner,
                    retainCurves: false);
                StageCall(ref _callsRefineMc);
                Interlocked.Add(ref _stageMcCallTicks, Stopwatch.GetTimestamp() - nullMcStart);

                edgePct = (mcResult.TerminalBalanceMean - nullResult.TerminalBalanceMean)
                          / config.StartingCapital * 100.0;
                nullMean = nullResult.TerminalBalanceMean;
                nullIters = config.RefineIterations;
            }

            versions[i] = v with
            {
                Mc = BuildMcSummary(mcResult, edgePct, nullMean, nullIters),
                TerminalBalances = SanitizeArray(mcResult.TerminalBalances),
                RepresentativeCurve = DownsampleCurve(mcResult),
                CurvesRetained = mcResult.CurvesRetained
            };

            int done = Interlocked.Increment(ref refineDone);

            progress?.Report(new OptimizationProgress(
                "Refine MC", done, survivors.Count,
                $"Refine {done}/{survivors.Count}",
                eta.Remaining()));

            Interlocked.Add(ref _stageRefineVersionTicks, Stopwatch.GetTimestamp() - versionStart);
        });

        refineMs = TicksToMs(Stopwatch.GetTimestamp() - refineStart);

        progress?.Report(new OptimizationProgress("Save", 0, 1, "Saving batch...", eta.Remaining()));

        var batch = new OptimizationBatch(
            Id: batchId,
            StrategyName: strategyName,
            StrategyPath: strategyPath,
            Symbol: symbol,
            Timeframe: timeframe,
            RunUtc: DateTime.UtcNow,
            CoarseIterations: config.CoarseIterations,
            RefineIterations: config.RefineIterations,
            RefineTopN: config.RefineTopN,
            StartingCapital: config.StartingCapital,
            DrawdownType: config.DrawdownType,
            MaxLossPercent: config.MaxLossPercent,
            MaxDailyLossPercent: config.MaxDailyLossPercent,
            ProfitTargetPercent: config.ProfitTargetPercent,
            BestDayRulePercent: config.BestDayRulePercent,
            CombinationCount: comboCount,
            DataSpanYears: dataSpanYears,
            Versions: versions);

        long saveStart = Stopwatch.GetTimestamp();
        await _store.SaveAsync(batch, ct).ConfigureAwait(false);
        saveMs = TicksToMs(Stopwatch.GetTimestamp() - saveStart);

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: complete in {wall.ElapsedMilliseconds} ms " +
            $"(estimate was {eta.TotalEstimateSeconds:F1}s)");

        progress?.Report(new OptimizationProgress(
            "Done", 1, 1,
            $"Optimization complete - {comboCount} version(s) in {wall.Elapsed.TotalSeconds:F1}s",
            eta.Remaining()));

        return batch;

        }
        finally
        {
            // Phase 29A — one summary line per instrumented stage, emitted
            // once per run on both the success and the cancel path.
            // Per-iteration logging was deliberately avoided: 2000+ lines
            // would flood the log and add measurable I/O to the thing being
            // measured.
            wall.Stop();
            EmitStageSummary(batchId, wall.Elapsed, setupMs, backtestMs, coarseMs, refineMs, saveMs, comboCount, survivorCount);
        }
    }

    // ------------------------------------------------------------------
    // Phase 29A instrumentation helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// Clears the stage accumulators. OptimizationRunner is a DI singleton,
    /// so without this a second Optimize pass would inherit the first pass's
    /// numbers.
    /// </summary>
    private void ResetStageTimers()
    {
        _callsSliceBuild = 0;
        _callsCoarseMc = 0;
        _callsRefineMc = 0;
        _callsNullGenerate = 0;

        _stageBacktestTicks = 0;
        _stageCompileLookupTicks = 0;
        _stageSliceBuildTicks = 0;
        _stageMcCallTicks = 0;
        _stageNullGenerateTicks = 0;

        _stageCompileLookupVersionTicks = 0;
        _stageCoarseVersionTicks = 0;
        _stageRefineVersionTicks = 0;

        _msBacktest = 0;
        _msDiskIo = 0;
        _msSliceBuild = 0;
        _msMcCall = 0;
        _msCoarseVersion = 0;
        _msRefineVersion = 0;
        _msNullGenerate = 0;

        _msCompile = 0;
        _msCacheLookup = 0;
        _msSourceRead = 0;
        _msStat = 0;
        _msGlobals = 0;
        _msInvoke = 0;
        _msInvokePlumbing = 0;
        _msInvokeCall = 0;

        _compileCalls = 0;
        _cacheHits = 0;
        _cacheMisses = 0;
        _instrumentedVersions = 0;

        // Phase 30: the stage accumulators live on the StrategyManager
        // singleton, so they need the same per-run reset.
        _strat.ResetInstrumentation();
    }

    /// <summary>
    /// Counts one invocation of a stage. Reached from the parallel phases, so
    /// the increment is atomic.
    /// </summary>
    private static void StageCall(ref long invocationCount)
        => Interlocked.Increment(ref invocationCount);

    private void EmitStageSummary(
        string batchId,
        TimeSpan wall,
        double setupMs,
        double backtestMs,
        double coarseMs,
        double refineMs,
        double saveMs,
        int comboCount,
        int survivorCount)
    {
        ConvertStageTimestampsToMs();

        double totalMs = wall.TotalMilliseconds;

        // `covered` is the sum of the phase wall spans. Setup, backtest,
        // coarse, refine and save each occupy a distinct stretch of the
        // orchestration thread, so they partition wall time into consecutive
        // phases. The per-version and per-MC-call spans are summed across
        // their workers and are therefore excluded here; they say how much
        // CPU each phase consumed, not how much wall time it owned.
        double coveredMs = setupMs + backtestMs + coarseMs + refineMs + saveMs;

        LogManager.Info(LogTag.Runner,
            $"[29A] optimization {batchId}: total wall {totalMs:F0} ms, " +
            $"Environment.ProcessorCount {Environment.ProcessorCount}, " +
            $"combinations {comboCount}, survivors {survivorCount}");

        LogManager.Info(LogTag.Runner,
            $"[29A]   phase coverage: {coveredMs:F0} ms ({Percent(coveredMs, totalMs)} of wall), " +
            $"unaccounted {Math.Max(0.0, totalMs - coveredMs):F0} ms");

        // One line per stage, never one per invocation. The per-version and
        // per-MC-call spans are summed across every concurrent worker, so
        // they can legitimately exceed wall time (and 100%) when their
        // parallelism is wider than one core. That overshoot is the
        // parallelism itself, not a measurement error.
        LogStage("Setup (grid + topology + ETA)", setupMs, 1, totalMs);
        LogStage("Backtest phase (wall)", backtestMs, comboCount, totalMs);
        LogStage("  of which per-version backtest span (thread-summed)", _msBacktest, comboCount, totalMs);
        LogStage("StrategyManager overhead (stat + compile-cache lookup + source read, derived)",
            _msDiskIo, comboCount, totalMs);
        LogStage("TimeSliceBuilder.Build", _msSliceBuild, _callsSliceBuild, totalMs);
        LogStage("Coarse MC phase (wall)", coarseMs, comboCount, totalMs);
        LogStage("Refine MC phase (wall)", refineMs, survivorCount, totalMs);
        LogStage("MC engine calls (coarse + refine, thread-summed)", _msMcCall,
            _callsCoarseMc + _callsRefineMc, totalMs);
        LogStage("NullModelGenerator.GenerateRandomSignalTrades", _msNullGenerate, _callsNullGenerate, totalMs);
        LogStage("Save/persist (OptimizationStore.SaveAsync)", saveMs, 1, totalMs);

        LogManager.Info(LogTag.Runner,
            $"[29A]   version detail: backtest {comboCount} call(s), " +
            $"coarse-version {comboCount} ({_msCoarseVersion:F0} ms summed), " +
            $"refine-version {survivorCount} ({_msRefineVersion:F0} ms summed), " +
            $"derived StrategyManager residual {_msDiskIo:F0} ms " +
            $"over {_stageCompileLookupVersionTicks} version(s)");

        EmitRoslynSummary(batchId, totalMs, comboCount);
    }

    // ------------------------------------------------------------------
    // Phase 30 — Roslyn compile vs execute split
    //
    // The 29A numbers above localise the wall time to the backtest phase and
    // leave only a 38 ms derived residual inside StrategyManager (29A's warm
    // run). Everything else in that phase is inside ScriptRunner: Roslyn
    // compile plus script invoke. This block splits it. It reads
    // StrategyManager's accumulators and reports them against the backtest
    // phase wall span, because that is the span the split has to explain —
    // not total wall.
    //
    // Like the 29A per-stage spans, the stage numbers here are summed across
    // every concurrent worker and may exceed the backtest wall (and 100%).
    // That overshoot is the 20-way parallelism, not a measurement error; the
    // percentages answer "of the backtest wall, how much did this stage
    // cost", and the split between the stages is what matters.
    // ------------------------------------------------------------------

    private void EmitRoslynSummary(string batchId, double totalMs, int comboCount)
    {
        var snap = _strat.GetInstrumentationSnapshot();

        _instrumentedVersions = snap.Versions;
        _cacheHits = snap.CacheHits;
        _cacheMisses = snap.CacheMisses;
        _compileCalls = snap.CompileCalls;

        _msCompile = TicksToMs(snap.CompileTicks);
        _msCacheLookup = TicksToMs(snap.CacheLookupTicks);
        _msSourceRead = TicksToMs(snap.SourceReadTicks);
        _msStat = TicksToMs(snap.StatTicks);
        _msGlobals = TicksToMs(snap.GlobalsTicks);
        _msInvoke = TicksToMs(snap.InvokeTicks);
        _msInvokePlumbing = TicksToMs(snap.InvokePlumbingTicks);
        _msInvokeCall = TicksToMs(snap.InvokeCallTicks);

        double backtestMs = _msBacktest;

        // What StrategyManager wrapped itself, and therefore all of it
        // except any remaining unmeasured residue.
        double wrappedMs = _msCompile + _msCacheLookup + _msSourceRead + _msStat + _msGlobals + _msInvoke;
        double residualMs = backtestMs - wrappedMs;

        double perVersionInvoke = _instrumentedVersions > 0
            ? _msInvoke / _instrumentedVersions
            : 0.0;

        LogManager.Info(LogTag.Runner,
            $"[30] optimization {batchId}: backtest phase {backtestMs:F0} ms thread-summed over " +
            $"{comboCount} combination(s); compile {_msCompile:F0} ms, cache lookup {_msCacheLookup:F0} ms, " +
            $"source read {_msSourceRead:F0} ms, stat {_msStat:F0} ms, globals setup {_msGlobals:F0} ms, " +
            $"invoke {_msInvoke:F0} ms");

        LogManager.Info(LogTag.Runner,
            $"[30]   split of backtest thread time: compile {Percent(_msCompile, backtestMs)}, " +
            $"cache lookup {Percent(_msCacheLookup, backtestMs)}, source read {Percent(_msSourceRead, backtestMs)}, " +
            $"stat {Percent(_msStat, backtestMs)}, globals setup {Percent(_msGlobals, backtestMs)}, " +
            $"invoke {Percent(_msInvoke, backtestMs)}, unwrapped residual {residualMs:F0} ms");

        LogManager.Info(LogTag.Runner,
            $"[30]   cache: {_cacheHits} hit(s) / {_cacheMisses} miss(es) over {_instrumentedVersions} version(s), " +
            $"{_compileCalls} real compile(s); invoke {perVersionInvoke:F2} ms/version; " +
            $"of total wall {totalMs:F0} ms the backtest phase owns the span but not the wall " +
            $"({Percent(backtestMs, totalMs)} in thread-summed terms)");

        EmitInvokeDecomposition(batchId, totalMs);
    }

    // ------------------------------------------------------------------
    // Phase 33 — decomposition of the Roslyn invoke cost
    //
    // Phase 30 put 76% of the warm backtest invoke inside `runner(globals)`
    // and could not see past the delegate boundary. From outside the
    // scripting API there is exactly one further seam: between building the
    // call's arguments and making the call. This block reports both halves,
    // per version and as a share of the invoke span, so the next phase can
    // tell an argument-construction cost from a Roslyn-internal one.
    //
    // The two halves are additive with the [30] invoke number by
    // construction (same start stamp, same end stamp), and like every other
    // per-version span here they are summed across the 20 concurrent
    // backtest workers, so they may exceed the wall and 100%.
    //
    // The call half is NOT a single Roslyn stage and is not reported as one:
    // the delegate is a `ScriptRunner<object?>`, and its invocation performs
    // execution-state construction, the globals handoff, the compiled
    // submission call and the task-await continuation internally. The
    // scripting API exposes no hook between them, so the split inside the
    // call is invisible from every call site in this project.
    // ------------------------------------------------------------------

    private void EmitInvokeDecomposition(string batchId, double totalMs)
    {
        double plumbingMs = _msInvokePlumbing;
        double callMs = _msInvokeCall;
        double subdivMs = plumbingMs + callMs;
        double unpricedMs = Math.Max(0.0, _msInvoke - subdivMs);

        double perVersionPlumbing = _instrumentedVersions > 0 ? plumbingMs / _instrumentedVersions : 0.0;
        double perVersionCall = _instrumentedVersions > 0 ? callMs / _instrumentedVersions : 0.0;
        double perVersionInvoke = _instrumentedVersions > 0 ? _msInvoke / _instrumentedVersions : 0.0;

        LogManager.Info(LogTag.Runner,
            $"[33] optimization {batchId}: backtest invoke {_msInvoke:F0} ms thread-summed over " +
            $"{_instrumentedVersions} version(s); pre-invoke plumbing {plumbingMs:F0} ms " +
            $"({Percent(plumbingMs, _msInvoke)} of invoke), delegate call {callMs:F0} ms " +
            $"({Percent(callMs, _msInvoke)} of invoke)");

        LogManager.Info(LogTag.Runner,
            $"[33]   per version: invoke {perVersionInvoke:F2} ms, plumbing {perVersionPlumbing:F2} ms " +
            $"({Percent(plumbingMs, _msInvoke)}), delegate call {perVersionCall:F2} ms " +
            $"({Percent(callMs, _msInvoke)}); outside the [29A]/[30] stage set the invoke is " +
            $"{Percent(_msInvoke, totalMs)} of total wall ({totalMs:F0} ms), thread-summed");

        LogManager.Info(LogTag.Runner,
            $"[33]   additivity: plumbing + call {subdivMs:F0} ms against [30] invoke {_msInvoke:F0} ms, " +
            $"unpriced call-site handoff {unpricedMs:F0} ms " +
            $"({Percent(unpricedMs, _msInvoke)}); the call half is one undivided " +
            $"ScriptRunner invocation — execution state, globals handoff, submission call and await " +
            $"continuation are not separable from any call site");
    }

    /// <summary>
    /// Percent of wall time, or `n/a` when the run recorded no wall time
    /// (zero-combination runs, and any run whose Stopwatch never advanced).
    /// </summary>
    private static string Percent(double elapsedMs, double totalMs)
        => totalMs > 0.0 ? $"{(elapsedMs / totalMs * 100.0):F1}%" : "n/a";

    private static void LogStage(string name, double elapsedMs, long invocations, double totalMs)
    {
        LogManager.Info(LogTag.Runner,
            $"[29A]   {name}: {elapsedMs:F0} ms, {Percent(elapsedMs, totalMs)} of wall, " +
            $"{invocations} invocation(s)");
    }

    private void ConvertStageTimestampsToMs()
    {
        _msBacktest = TicksToMs(_stageBacktestTicks);
        _msDiskIo = TicksToMs(_stageCompileLookupTicks);
        _msSliceBuild = TicksToMs(_stageSliceBuildTicks);
        _msMcCall = TicksToMs(_stageMcCallTicks);
        _msNullGenerate = TicksToMs(_stageNullGenerateTicks);
        _msCoarseVersion = TicksToMs(_stageCoarseVersionTicks);
        _msRefineVersion = TicksToMs(_stageRefineVersionTicks);
    }

    private static double TicksToMs(long ticks)
        => ticks * 1000.0 / Stopwatch.Frequency;

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static double ComputeDataSpanYears(List<Kline> data)
    {
        if (data == null || data.Count < 2) return 0.0;

        var first = DateTime.SpecifyKind(data[0].Timestamp, DateTimeKind.Utc);
        var last  = DateTime.SpecifyKind(data[data.Count - 1].Timestamp, DateTimeKind.Utc);
        double days = (last - first).TotalDays;

        return days > 1.0 ? days / 365.25 : 0.0;
    }

    private static double Safe(double v)
        => double.IsNaN(v) || double.IsInfinity(v) ? 0.0 : v;

    private static double[] SanitizeArray(double[] src)
    {
        if (src == null || src.Length == 0) return Array.Empty<double>();
        var dst = new double[src.Length];
        for (int i = 0; i < src.Length; i++) dst[i] = Safe(src[i]);
        return dst;
    }

    private static McSummary BuildMcSummary(
        MonteCarloResult r,
        double edgePercent,
        double nullMean,
        int nullIterations)
    {
        return new McSummary(
            PRuin: Safe(r.PRuin),
            PFunded: Safe(r.PFunded),
            PTimeout: Safe(r.PTimeout),
            ExpectedSlicesToFund: Safe(r.ExpectedSlicesToFund),
            ExpectedSlicesToRuin: Safe(r.ExpectedSlicesToRuin),
            TerminalBalanceMean: Safe(r.TerminalBalanceMean),
            TerminalBalanceMedian: Safe(r.TerminalBalanceMedian),
            TerminalBalanceStdDev: Safe(r.TerminalBalanceStdDev),
            TerminalBalanceP05: Safe(r.TerminalBalanceP05),
            TerminalBalanceP95: Safe(r.TerminalBalanceP95),
            MaxDrawdownMean: Safe(r.MaxDrawdownMean),
            MaxDrawdownMedian: Safe(r.MaxDrawdownMedian),
            MaxDrawdownP05: Safe(r.MaxDrawdownP05),
            MaxDrawdownP95: Safe(r.MaxDrawdownP95),
            ProbBestDayViolation: Safe(r.ProbBestDayViolation),
            BestDayRatioMedian: Safe(r.BestDayRatioMedian),
            BestDayRatioP95: Safe(r.BestDayRatioP95),
            MedianLongestLosingStreak: r.MedianLongestLosingStreak,
            P95LongestLosingStreak: r.P95LongestLosingStreak,
            MedianLongestFlatStretch: r.MedianLongestFlatStretch,
            P95LongestFlatStretch: r.P95LongestFlatStretch,
            RuinByDailyFloorCount: r.RuinByDailyFloorCount,
            RuinByMaxFloorCount: r.RuinByMaxFloorCount,
            EdgePercent: Safe(edgePercent),
            NullMeanTerminalBalance: Safe(nullMean),
            NullIterations: nullIterations);
    }

    private static double[] DownsampleCurve(MonteCarloResult r)
    {
        if (!r.CurvesRetained || r.EquityCurves.Length == 0 || r.TerminalBalances.Length == 0)
            return Array.Empty<double>();

        double median = r.TerminalBalanceMedian;
        int best = 0;
        double bestDiff = double.MaxValue;
        int len = Math.Min(r.TerminalBalances.Length, r.EquityCurves.Length);

        for (int i = 0; i < len; i++)
        {
            double diff = Math.Abs(r.TerminalBalances[i] - median);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }

        var curve = r.EquityCurves[best];
        if (curve == null || curve.Length == 0) return Array.Empty<double>();
        if (curve.Length <= RepresentativeCurveLength) return SanitizeArray(curve);

        var result = new double[RepresentativeCurveLength];
        double step = (double)(curve.Length - 1) / (RepresentativeCurveLength - 1);
        for (int i = 0; i < RepresentativeCurveLength; i++)
        {
            int idx = (int)Math.Round(i * step);
            if (idx >= curve.Length) idx = curve.Length - 1;
            result[i] = Safe(curve[idx]);
        }
        return result;
    }

    private static int ComputeTargetSlices(IReadOnlyList<Kline> data, int tradingPeriodDays)
    {
        if (data == null || data.Count < 2) return tradingPeriodDays;

        long firstMs = new DateTimeOffset(data[0].Timestamp).ToUnixTimeMilliseconds();
        long lastMs = new DateTimeOffset(data[data.Count - 1].Timestamp).ToUnixTimeMilliseconds();
        double spanDays = Math.Max((lastMs - firstMs) / 86_400_000.0, 1.0 / 24.0);
        double slicesPerDay = data.Count / spanDays;

        int slices = (int)Math.Round(tradingPeriodDays * slicesPerDay);
        return slices < 1 ? tradingPeriodDays : slices;
    }
}
