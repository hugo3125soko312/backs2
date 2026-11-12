using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Models;
using backs2.Core.Optimizer;

namespace backs2.Core.WalkForward;

/// <summary>
/// Full-grid IS optimize over one bar slice. Signature matches
/// <c>OptimizationRunner.RunAsync</c> so the production wiring can pass
/// the real runner in directly. v1 also passes it for OOS via a one-combo
/// grid; the OOS delegate lets us swap to a thin single-version API later.
/// </summary>
public delegate Task<OptimizationBatch> IsWindowRunner(
    string strategyPath, string strategyName, string symbol, string timeframe,
    List<Kline> data, IReadOnlyList<GridParameter> grid,
    OptimizationRunConfig config, IProgress<OptimizationProgress>? progress,
    CancellationToken ct);

/// <summary>
/// Single-combination OOS evaluation. Returns one <see cref="OptimizationVersion"/>.
/// </summary>
public delegate Task<OptimizationVersion> OosWindowEvaluator(
    string strategyPath, string strategyName, string symbol, string timeframe,
    List<Kline> data, ParameterSet[] parameters,
    OptimizationRunConfig config, int mcIterations,
    CancellationToken ct);

/// <summary>
/// Orchestrates a walk-forward run: plan windows, optimize each IS slice,
/// pick the winner(s), evaluate on the OOS slice, aggregate.
///
/// <para>Both IS and OOS are supplied as delegates. This is the "hard seam"
/// in the design doc (§6): v1 wires OOS to a one-combination full run;
/// v2 can swap in a thin per-version API without touching this class.</para>
///
/// <para>The runner does not persist anything. It returns the completed
/// <see cref="WalkForwardBatch"/>; the caller saves it through
/// <see cref="WalkForwardStore"/>. This mirrors how
/// <c>RunnerViewModel.OptimizeAsync</c> handles batches today.</para>
/// </summary>
public sealed class WalkForwardRunner
{
    private const int MaxOosCurvePoints = WalkForwardAggregator.MaxWindowCurvePoints;

    private readonly IsWindowRunner _runIs;
    private readonly OosWindowEvaluator _evalOos;

    public WalkForwardRunner(IsWindowRunner runIs, OosWindowEvaluator evalOos)
    {
        _runIs   = runIs   ?? throw new ArgumentNullException(nameof(runIs));
        _evalOos = evalOos ?? throw new ArgumentNullException(nameof(evalOos));
    }

    public async Task<WalkForwardBatch> RunAsync(
        string strategyPath,
        string strategyName,
        string symbol,
        string timeframe,
        string datasetLabel,
        List<Kline> data,
        IReadOnlyList<GridParameter> grid,
        WalkForwardRunConfig config,
        IProgress<WalkForwardProgress>? progress,
        CancellationToken ct = default)
    {
        if (data == null || data.Count < 2)
            throw new ArgumentException("Walk-forward needs a non-empty data series.", nameof(data));
        if (config == null) throw new ArgumentNullException(nameof(config));
        if (grid == null || grid.Count == 0)
            throw new ArgumentException("At least one parameter is required.", nameof(grid));

        var plan = WalkForwardWindowPlanner.Plan(data.Count, config);
        int total = plan.Count;

        var windows = new List<WalkForwardWindowResult>(total);
        bool cancelled = false;
        var wall = Stopwatch.StartNew();

        // Snapshot the parameter names in first-seen order so the CSV
        // export and the stability view have a stable column layout.
        var parameterNames = grid.Select(g => g.Name).ToArray();

        for (int i = 0; i < total; i++)
        {
            if (ct.IsCancellationRequested) { cancelled = true; break; }

            var p = plan[i];
            var isData  = Slice(data, p.IsStartBar,  p.IsEndBar);
            var oosData = Slice(data, p.OosStartBar, p.OosEndBar);

            progress?.Report(new WalkForwardProgress(
                WindowIndex: i,
                TotalWindows: total,
                Phase: WalkForwardPhase.Is,
                BestIsMetric: null,
                BestOosMetric: i > 0 ? LastSuccess(windows)?.OosObjective : null,
                Fraction: (double)i / total,
                EtaSeconds: EstimateEta(wall, i, total)));

            var isProgress = new Progress<OptimizationProgress>(op =>
            {
                progress?.Report(new WalkForwardProgress(
                    WindowIndex: i,
                    TotalWindows: total,
                    Phase: WalkForwardPhase.Is,
                    BestIsMetric: null,
                    BestOosMetric: null,
                    Fraction: ((double)i + 0.5 * op.Fraction) / total,
                    EtaSeconds: EstimateEta(wall, i, total)));
            });

            var windowStart = Stopwatch.GetTimestamp();

            OptimizationBatch isBatch;
            try
            {
                isBatch = await _runIs(
                    strategyPath, strategyName, symbol, timeframe,
                    isData, grid, config.Optimization, isProgress, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            // Rank the IS results by the chosen objective, descending.
            var ranked = isBatch.Versions
                .Where(v => v.BacktestSuccess && v.Mc != null)
                .OrderByDescending(v => WalkForwardObjective.Select(config.Objective, v))
                .ThenByDescending(v => v.TradeCount)
                .ThenBy(v => v.Index)
                .ToList();

            if (ranked.Count == 0)
            {
                windows.Add(FailedWindow(p, data, isBatch.Versions.Length,
                    "No valid IS combination (all failed or MC did not run).",
                    Stopwatch.GetElapsedTime(windowStart)));
                continue;
            }

            int carry = Math.Max(1, Math.Min(config.CarryTopN, ranked.Count));
            var winners = ranked.Take(carry).ToList();
            var winner0 = winners[0];

            progress?.Report(new WalkForwardProgress(
                WindowIndex: i,
                TotalWindows: total,
                Phase: WalkForwardPhase.Oos,
                BestIsMetric: WalkForwardObjective.Select(config.Objective, winner0),
                BestOosMetric: null,
                Fraction: ((double)i + 0.75) / total,
                EtaSeconds: EstimateEta(wall, i, total)));

            // Evaluate each winner on the OOS slice.
            var oosVersions = new List<OptimizationVersion>(winners.Count);
            foreach (var w in winners)
            {
                try
                {
                    var oosV = await _evalOos(
                        strategyPath, strategyName, symbol, timeframe,
                        oosData, w.Parameters, config.Optimization,
                        mcIterations: config.Optimization.RefineIterations,
                        ct).ConfigureAwait(false);
                    oosVersions.Add(oosV);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    cancelled = true;
                    break;
                }
            }

            if (cancelled) break;
            if (oosVersions.Count == 0)
            {
                windows.Add(FailedWindow(p, data, ranked.Count,
                    "OOS evaluation produced no result.",
                    Stopwatch.GetElapsedTime(windowStart)));
                continue;
            }

            var oos0 = oosVersions[0];
            double isMetric  = WalkForwardObjective.Select(config.Objective, winner0);
            double oosMetric = WalkForwardObjective.Select(config.Objective, oos0);

            var extras = new WalkForwardCandidate[Math.Max(0, oosVersions.Count - 1)];
            for (int k = 1; k < oosVersions.Count; k++)
            {
                extras[k - 1] = new WalkForwardCandidate(
                    IsRank: k + 1,
                    Parameters: oosVersions[k].Parameters,
                    IsObjective: WalkForwardObjective.Select(config.Objective, winners[k]),
                    OosObjective: WalkForwardObjective.Select(config.Objective, oosVersions[k]),
                    OosTradeCount: oosVersions[k].TradeCount,
                    OosNetPnl: oosVersions[k].NetPnl);
            }

            var curve = oos0.RepresentativeCurve ?? Array.Empty<double>();
            if (curve.Length > MaxOosCurvePoints)
                curve = WalkForwardAggregator.Downsample(curve, MaxOosCurvePoints);

            windows.Add(new WalkForwardWindowResult(
                Index: i,
                IsStartBar: p.IsStartBar,
                IsEndBar:   p.IsEndBar,
                OosStartBar: p.OosStartBar,
                OosEndBar:   p.OosEndBar,
                IsStartUtc:  data[p.IsStartBar].Timestamp.ToUniversalTime(),
                IsEndUtc:    data[p.IsEndBar - 1].Timestamp.ToUniversalTime(),
                OosStartUtc: data[p.OosStartBar].Timestamp.ToUniversalTime(),
                OosEndUtc:   data[p.OosEndBar - 1].Timestamp.ToUniversalTime(),
                ChosenParameters: winner0.Parameters,
                IsObjective: isMetric,
                OosObjective: oosMetric,
                Degradation: oosMetric - isMetric,
                OosTradeCount: oos0.TradeCount,
                OosNetPnl: oos0.NetPnl,
                OosMaxDrawdown: oos0.Mc?.MaxDrawdownMedian ?? 0.0,
                OosTerminalBalance: oos0.Mc?.TerminalBalanceMedian ?? 0.0,
                OosBeatNull: (oos0.Mc?.EdgePercent ?? 0.0) > 0.0,
                OosMc: oos0.Mc,
                OosEquityCurve: curve,
                ExtraCandidates: extras,
                IsCombinationsEvaluated: isBatch.Versions.Length,
                Success: true,
                Error: null,
                DurationMs: (long)Stopwatch.GetElapsedTime(windowStart).TotalMilliseconds));
        }

        progress?.Report(new WalkForwardProgress(
            WindowIndex: total,
            TotalWindows: total,
            Phase: WalkForwardPhase.Aggregate,
            BestIsMetric: null,
            BestOosMetric: null,
            Fraction: 1.0,
            EtaSeconds: 0.0));

        var summary = WalkForwardAggregator.Aggregate(windows);
        double dataSpanYears = ComputeSpanYears(data);

        return new WalkForwardBatch(
            SchemaVersion: WalkForwardBatch.CurrentSchema,
            Id: $"{DateTime.UtcNow:yyyyMMdd'T'HHmmss}",
            StrategyName: strategyName,
            StrategyPath: strategyPath,
            Symbol: symbol,
            Timeframe: timeframe,
            DatasetLabel: datasetLabel ?? "",
            RunUtc: DateTime.UtcNow,
            TotalBars: data.Count,
            DataSpanYears: dataSpanYears,
            CombinationCount: 0,
            Cancelled: cancelled,
            ParameterNames: parameterNames,
            Config: config,
            Windows: windows.ToArray(),
            Summary: summary);
    }

    private static WalkForwardWindowResult? LastSuccess(List<WalkForwardWindowResult> w)
    {
        for (int i = w.Count - 1; i >= 0; i--)
            if (w[i].Success) return w[i];
        return null;
    }

    private static List<Kline> Slice(List<Kline> data, int start, int endExclusive)
    {
        int len = endExclusive - start;
        return data.GetRange(start, len);
    }

    private static double EstimateEta(Stopwatch sw, int completed, int total)
    {
        if (completed <= 0) return -1.0;
        double per = sw.Elapsed.TotalSeconds / completed;
        return per * (total - completed);
    }

    private static double ComputeSpanYears(List<Kline> data)
    {
        if (data.Count < 2) return 0.0;
        var span = data[data.Count - 1].Timestamp - data[0].Timestamp;
        return span.TotalDays / 365.25;
    }

    private static WalkForwardWindowResult FailedWindow(
        WalkForwardWindowPlan p, List<Kline> data, int combos, string error, TimeSpan elapsed)
    {
        return new WalkForwardWindowResult(
            Index: p.Index,
            IsStartBar: p.IsStartBar, IsEndBar: p.IsEndBar,
            OosStartBar: p.OosStartBar, OosEndBar: p.OosEndBar,
            IsStartUtc:  data[p.IsStartBar].Timestamp.ToUniversalTime(),
            IsEndUtc:    data[p.IsEndBar - 1].Timestamp.ToUniversalTime(),
            OosStartUtc: data[p.OosStartBar].Timestamp.ToUniversalTime(),
            OosEndUtc:   data[p.OosEndBar - 1].Timestamp.ToUniversalTime(),
            ChosenParameters: Array.Empty<ParameterSet>(),
            IsObjective: 0.0, OosObjective: 0.0, Degradation: 0.0,
            OosTradeCount: 0, OosNetPnl: 0.0,
            OosMaxDrawdown: 0.0, OosTerminalBalance: 0.0, OosBeatNull: false,
            OosMc: null, OosEquityCurve: Array.Empty<double>(),
            ExtraCandidates: Array.Empty<WalkForwardCandidate>(),
            IsCombinationsEvaluated: combos,
            Success: false,
            Error: error,
            DurationMs: (long)elapsed.TotalMilliseconds);
    }
}
