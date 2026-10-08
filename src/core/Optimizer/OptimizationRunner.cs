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

/// <summary>
/// Configuration for one optimization run. Set by the Runner's UI at run
/// start. Account rules come from the active AccountService selection;
/// MC iteration counts come from the Runner's settings panel.
/// </summary>
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

/// <summary>
/// Executes an optimization run end to end:
///   1. Generate the parameter grid (ParameterGridBuilder).
///   2. Parallel backtests across every combination (StrategyManager).
///   3. Sequential coarse Monte Carlo per version (MonteCarloEngine).
///   4. Sequential refine Monte Carlo on the top-N survivors.
///   5. Persist the batch (OptimizationStore).
///
/// ETA is estimated internally on every progress event using an
/// EtaEstimator that calibrates against the first EtaEstimator.BlindWindow
/// versions before extrapolating.
///
/// Trade lists never touch the serializable OptimizationVersion record.
/// They live in a parallel array indexed by combination index for the
/// duration of the run, then are dropped once MC completes.
/// </summary>
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
        var batchId = DateTime.UtcNow.ToString("HHmmss").Length > 0
            ? DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")
            : DateTime.UtcNow.ToString("yyyyMMdd-HHmmss");

        var eta = new EtaEstimator { GpuFactor = config.GpuFactor <= 0 ? 1.0 : config.GpuFactor };

        // --- 1. Grid ---
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
        var versions = new OptimizationVersion[comboCount];
        var tradesByVersion = new IReadOnlyList<Trade>[comboCount];

        // --- 2. Parallel backtests ---
        bool savedSerialize = _strat.SerializeBacktests;
        _strat.SerializeBacktests = false;

        try
        {
            int completedBacktests = 0;
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = Environment.ProcessorCount,
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

                    versions[i] = new OptimizationVersion(
                        Index: i,
                        Parameters: paramSet,
                        BacktestSuccess: result.Success,
                        BacktestError: result.Error,
                        BacktestDurationMs: (long)result.Duration.TotalMilliseconds,
                        TradeCount: result.Trades.Count,
                        McRan: false,
                        Mc: null,
                        TerminalBalances: Array.Empty<double>(),
                        RepresentativeCurve: Array.Empty<double>(),
                        CurvesRetained: false);

                    eta.ReportBacktest((long)result.Duration.TotalMilliseconds);

                    int done = Interlocked.Increment(ref completedBacktests);
                    double? remaining = eta.Estimate(
                        remainingCombos: comboCount - done,
                        refineTopN: config.RefineTopN,
                        coarseIterations: config.CoarseIterations,
                        refineIterations: config.RefineIterations,
                        refineRemaining: config.RefineTopN);

                    progress?.Report(new OptimizationProgress(
                        "Backtest", done, comboCount,
                        $"Backtest {done}/{comboCount}",
                        remaining));
                }).ConfigureAwait(false);
        }
        finally
        {
            _strat.SerializeBacktests = savedSerialize;
        }

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: backtests complete in {wall.ElapsedMilliseconds} ms");

        int targetSlices = ComputeTargetSlices(data, config.TradingPeriodDays);

        // --- 3. Coarse MC, sequential per version ---
        for (int i = 0; i < comboCount; i++)
        {
            ct.ThrowIfCancellationRequested();

            var v = versions[i];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();

            if (!v.BacktestSuccess || trades.Count < 2)
            {
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", i + 1, comboCount,
                    $"Skipping version {i + 1}/{comboCount} — {trades.Count} trade(s)"));
                continue;
            }

            var slices = TimeSliceBuilder.Build(trades, data);
            if (slices.Length == 0)
            {
                progress?.Report(new OptimizationProgress(
                    "Coarse MC", i + 1, comboCount,
                    $"Skipping version {i + 1}/{comboCount} — no time slices"));
                continue;
            }

            var mcSw = Stopwatch.StartNew();
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
                cancellationToken: ct);
            mcSw.Stop();
            eta.ReportMc(mcSw.ElapsedMilliseconds);

            versions[i] = v with
            {
                McRan = true,
                Mc = BuildMcSummary(mcResult, edgePercent: double.NaN, nullMean: double.NaN, nullIterations: 0),
                TerminalBalances = mcResult.TerminalBalances,
                RepresentativeCurve = DownsampleCurve(mcResult),
                CurvesRetained = mcResult.CurvesRetained
            };

            int coarseRemaining = comboCount - (i + 1);
            double? remainingSeconds = eta.Estimate(
                remainingCombos: coarseRemaining,
                refineTopN: config.RefineTopN,
                coarseIterations: config.CoarseIterations,
                refineIterations: config.RefineIterations,
                refineRemaining: config.RefineTopN);

            progress?.Report(new OptimizationProgress(
                "Coarse MC", i + 1, comboCount,
                $"Coarse MC {i + 1}/{comboCount}",
                remainingSeconds));
        }

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: coarse MC complete at {wall.ElapsedMilliseconds} ms");

        // --- 4. Refine top-N survivors ---
        var survivors = versions
            .Select((v, i) => (Version: v, Index: i))
            .Where(t => t.Version.McRan && t.Version.Mc != null)
            .OrderByDescending(t => t.Version.Mc!.PFunded)
            .ThenByDescending(t => t.Version.Mc!.TerminalBalanceMedian)
            .Take(config.RefineTopN)
            .ToList();

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: refining top {survivors.Count} survivor(s) at {config.RefineIterations} iterations");

        for (int j = 0; j < survivors.Count; j++)
        {
            ct.ThrowIfCancellationRequested();

            var (v, i) = survivors[j];
            var trades = tradesByVersion[i] ?? Array.Empty<Trade>();
            var slices = TimeSliceBuilder.Build(trades, data);
            if (slices.Length == 0) continue;

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
                cancellationToken: ct);

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
                    cancellationToken: ct);

                edgePct = (mcResult.TerminalBalanceMean - nullResult.TerminalBalanceMean)
                          / config.StartingCapital * 100.0;
                nullMean = nullResult.TerminalBalanceMean;
                nullIters = config.RefineIterations;
            }

            versions[i] = v with
            {
                Mc = BuildMcSummary(mcResult, edgePct, nullMean, nullIters),
                TerminalBalances = mcResult.TerminalBalances,
                RepresentativeCurve = DownsampleCurve(mcResult),
                CurvesRetained = mcResult.CurvesRetained
            };

            double? remainingSeconds = eta.Estimate(
                remainingCombos: 0,
                refineTopN: config.RefineTopN,
                coarseIterations: config.CoarseIterations,
                refineIterations: config.RefineIterations,
                refineRemaining: survivors.Count - (j + 1));

            progress?.Report(new OptimizationProgress(
                "Refine MC", j + 1, survivors.Count,
                $"Refine {j + 1}/{survivors.Count}",
                remainingSeconds));
        }

        // --- 5. Persist ---
        progress?.Report(new OptimizationProgress("Save", 0, 1, "Saving batch..."));

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
            Versions: versions);

        await _store.SaveAsync(batch, ct).ConfigureAwait(false);

        wall.Stop();
        progress?.Report(new OptimizationProgress(
            "Done", 1, 1,
            $"Optimization complete — {comboCount} version(s) in {wall.Elapsed.TotalSeconds:F1}s"));

        LogManager.Info(LogTag.Runner,
            $"optimization {batchId}: complete in {wall.ElapsedMilliseconds} ms");

        return batch;
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static McSummary BuildMcSummary(
        MonteCarloResult r,
        double edgePercent,
        double nullMean,
        int nullIterations)
    {
        return new McSummary(
            PRuin: r.PRuin,
            PFunded: r.PFunded,
            PTimeout: r.PTimeout,
            ExpectedSlicesToFund: r.ExpectedSlicesToFund,
            ExpectedSlicesToRuin: r.ExpectedSlicesToRuin,
            TerminalBalanceMean: r.TerminalBalanceMean,
            TerminalBalanceMedian: r.TerminalBalanceMedian,
            TerminalBalanceStdDev: r.TerminalBalanceStdDev,
            TerminalBalanceP05: r.TerminalBalanceP05,
            TerminalBalanceP95: r.TerminalBalanceP95,
            MaxDrawdownMean: r.MaxDrawdownMean,
            MaxDrawdownMedian: r.MaxDrawdownMedian,
            MaxDrawdownP05: r.MaxDrawdownP05,
            MaxDrawdownP95: r.MaxDrawdownP95,
            ProbBestDayViolation: r.ProbBestDayViolation,
            BestDayRatioMedian: r.BestDayRatioMedian,
            BestDayRatioP95: r.BestDayRatioP95,
            MedianLongestLosingStreak: r.MedianLongestLosingStreak,
            P95LongestLosingStreak: r.P95LongestLosingStreak,
            MedianLongestFlatStretch: r.MedianLongestFlatStretch,
            P95LongestFlatStretch: r.P95LongestFlatStretch,
            RuinByDailyFloorCount: r.RuinByDailyFloorCount,
            RuinByMaxFloorCount: r.RuinByMaxFloorCount,
            EdgePercent: edgePercent,
            NullMeanTerminalBalance: nullMean,
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
        if (curve.Length <= RepresentativeCurveLength) return (double[])curve.Clone();

        var result = new double[RepresentativeCurveLength];
        double step = (double)(curve.Length - 1) / (RepresentativeCurveLength - 1);
        for (int i = 0; i < RepresentativeCurveLength; i++)
        {
            int idx = (int)Math.Round(i * step);
            if (idx >= curve.Length) idx = curve.Length - 1;
            result[i] = curve[idx];
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