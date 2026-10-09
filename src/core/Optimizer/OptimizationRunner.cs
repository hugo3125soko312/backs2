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

                    var result = await _strat.RunBacktestFromFileAsync(strategyPath, data, paramDict, token)
                        .ConfigureAwait(false);

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

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: backtests complete in {wall.ElapsedMilliseconds} ms");

        int targetSlices = ComputeTargetSlices(data, config.TradingPeriodDays);

        // ------------------------------------------------------------------
        // Phase 2 - coarse MC, outer-parallel with adaptive inner degree.
        // Coarse curves are never consumed, so retainCurves is false here.
        // ------------------------------------------------------------------

        int coarseDone = 0;
        var coarseOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = coarseOuter,
            CancellationToken = ct
        };

        Parallel.For(0, comboCount, coarseOptions, i =>
        {
            ct.ThrowIfCancellationRequested();

            var v = versions[i];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();

            if (!v.BacktestSuccess || trades.Count < 2)
            {
                int d = Interlocked.Increment(ref coarseDone);
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", d, comboCount,
                    $"Coarse MC {d}/{comboCount} (skipped)",
                    eta.Remaining()));
                return;
            }

            var slices = TimeSliceBuilder.Build(trades, data);
            if (slices.Length == 0)
            {
                int d = Interlocked.Increment(ref coarseDone);
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", d, comboCount,
                    $"Coarse MC {d}/{comboCount} (no slices)",
                    eta.Remaining()));
                return;
            }

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
        });

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: coarse MC complete at {wall.ElapsedMilliseconds} ms");

        var survivors = versions
            .Select((v, i) => (Version: v, Index: i))
            .Where(t => t.Version.McRan && t.Version.Mc != null)
            .OrderByDescending(t => t.Version.Mc!.PFunded)
            .ThenByDescending(t => t.Version.Mc!.TerminalBalanceMedian)
            .Take(config.RefineTopN)
            .ToList();

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

        int refineDone = 0;
        var refineOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = refineOuter,
            CancellationToken = ct
        };

        Parallel.For(0, survivors.Count, refineOptions, j =>
        {
            ct.ThrowIfCancellationRequested();

            var (v, i) = survivors[j];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();
            var slices = TimeSliceBuilder.Build(trades, data);
            if (slices.Length == 0)
            {
                Interlocked.Increment(ref refineDone);
                return;
            }

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

            double edgePct = double.NaN;
            double nullMean = double.NaN;
            int nullIters = 0;

            if (trades.Count >= EdgeMinTrades)
            {
                var nullTrades = NullModelGenerator.GenerateRandomSignalTrades(trades, EdgeNullSeed);
                var nullSlices = TimeSliceBuilder.Build(nullTrades, data);

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
        });

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

        await _store.SaveAsync(batch, ct).ConfigureAwait(false);

        wall.Stop();

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: complete in {wall.ElapsedMilliseconds} ms " +
            $"(estimate was {eta.TotalEstimateSeconds:F1}s)");

        progress?.Report(new OptimizationProgress(
            "Done", 1, 1,
            $"Optimization complete - {comboCount} version(s) in {wall.Elapsed.TotalSeconds:F1}s",
            eta.Remaining()));

        return batch;
    }

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