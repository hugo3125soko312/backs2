using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Computing;

namespace backs2.Core.Overview;

public sealed class MonteCarloEngine
{
    // Hard cap on iterations. 100 × the default (1000). Above this, the
    // compute time and CPU saturation make the app effectively unusable
    // regardless of memory.
    public const int MaxIterations = 100_000;

    // Ceiling on retained equity-curve cells (iterations × slices). Above
    // ~800 MB of curve doubles, curves are dropped from the result and
    // only the scalar stats arrays are kept.
    public const long MaxCurveCells = 100_000_000;

    private const int OutcomeBlown = MonteCarloOutcome.Blown;
    private const int OutcomeFunded = MonteCarloOutcome.Funded;
    private const int OutcomeTimeout = MonteCarloOutcome.Timeout;

    private readonly IComputeDispatcher _dispatcher;

    /// <param name="dispatcher">
    /// The compute façade that actually executes the simulations. It owns backend
    /// selection (CPU vs the DirectX 12 kernel) and chunking, so this class owns
    /// only the aggregation half: parameter derivation, the curve-retention
    /// ceiling, and the statistics.
    /// </param>
    public MonteCarloEngine(IComputeDispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    /// <summary>
    /// Runs N iterations of the time-domain bootstrap.
    ///
    /// <paramref name="maxDegreeOfParallelism"/> controls the inner
    /// parallelism over iterations:
    ///   -1 (default) — Environment.ProcessorCount (current behavior)
    ///    1           — sequential loop, no ThreadPool dispatch
    ///   N &gt; 1      — exactly N concurrent iterations
    ///
    /// The sequential fast-path exists for outer-parallel orchestrators
    /// (see OptimizationRunner.RunAsync) that already saturate the machine
    /// by running multiple versions concurrently. Sending the inner loop
    /// to the ThreadPool in that case only adds scheduling overhead.
    /// Topology decision documented in ROADMAP Phase 22E.
    ///
    /// <paramref name="retainCurves"/> is a caller-side gate on top of the
    /// MaxCurveCells ceiling. Pass false when the caller does not consume
    /// EquityCurves (coarse MC passes), so per-version allocations do not
    /// stack under outer parallelism. The ceiling always wins: if
    /// (iterations × targetDurationSlices) exceeds MaxCurveCells, curves are
    /// dropped regardless of this flag.
    /// </summary>
    public MonteCarloResult Run(
        TimeSlice[] slices,
        int targetDurationSlices,
        double startingCapital,
        string drawdownType,
        double maxDailyLossPercent,
        double maxLossPercent,
        double profitTargetPercent,
        int iterations = 1000,
        int expectedBlockSize = 5,
        double bestDayRulePercent = 50.0,
        int? seed = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default,
        int maxDegreeOfParallelism = -1,
        bool retainCurves = true)
    {
        if (slices == null || slices.Length == 0 || startingCapital <= 0 || targetDurationSlices <= 0)
        {
            return EmptyResult(
                startingCapital, expectedBlockSize, targetDurationSlices,
                drawdownType ?? "static", bestDayRulePercent);
        }

        if (iterations < 1) iterations = 1;
        if (iterations > MaxIterations) iterations = MaxIterations;
        if (expectedBlockSize < 1) expectedBlockSize = 1;
        if (bestDayRulePercent < 0) bestDayRulePercent = 0;

        long curveCells = (long)iterations * targetDurationSlices;
        bool keepCurves = retainCurves && curveCells <= MaxCurveCells;

        int dailyFloorLoss = (int)Math.Round(startingCapital * (maxDailyLossPercent / 100.0));
        int maxFloorLoss = (int)Math.Round(startingCapital * (maxLossPercent / 100.0));
        int profitTarget = (int)Math.Round(startingCapital * (profitTargetPercent / 100.0));

        bool trailingEod = string.Equals(drawdownType, "trailing-eod", StringComparison.OrdinalIgnoreCase);
        bool trailingIntraday = string.Equals(drawdownType, "trailing-intraday", StringComparison.OrdinalIgnoreCase);

        var simulation = new MonteCarloSimulationRequest(
            TargetDurationSlices: targetDurationSlices,
            StartingCapital: startingCapital,
            DailyFloorLoss: dailyFloorLoss,
            MaxFloorLoss: maxFloorLoss,
            ProfitTarget: profitTarget,
            ExpectedBlockSize: expectedBlockSize,
            TrailingEod: trailingEod,
            TrailingIntraday: trailingIntraday);

        var dispatch = new MonteCarloDispatchRequest(
            Simulation: simulation,
            Slices: slices,
            Iterations: iterations,
            Seed: seed,
            RetainCurves: keepCurves,
            MaxDegreeOfParallelism: maxDegreeOfParallelism);

        var computed = _dispatcher
            .DispatchMonteCarloAsync(dispatch, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        int completed = computed.Count;

        // A cancelled dispatch returns the chunks that finished, so the arrays
        // below are truncated rather than padded — the result describes exactly
        // the iterations that ran. Reporting stays at the requested cadence and
        // never claims a completion that did not happen.
        if (progress != null && completed > 0)
        {
            int progressStep = Math.Max(1, iterations / 20);
            for (int reported = progressStep; reported <= completed; reported += progressStep)
                progress.Report(reported);

            if (completed == iterations)
                progress.Report(completed);
        }

        var terminalBalances = computed.TerminalBalances;
        var outcomes = computed.Outcomes;
        var slicesElapsed = computed.SlicesElapsed;
        var maxDrawdowns = computed.MaxDrawdowns;
        var equityCurves = keepCurves && computed.CurvesRetained
            ? computed.EquityCurves
            : Array.Empty<double[]>();
        var bestDayRatios = computed.BestDayRatios;
        var longestLosingStreaks = computed.LongestLosingStreaks;
        var longestFlatStretches = computed.LongestFlatStretches;
        var ruinCauses = computed.RuinCauses;

        bool curvesRetained = keepCurves && computed.CurvesRetained;

        long curveBytes = curvesRetained
            ? (long)completed * targetDurationSlices * sizeof(double)
            : 0L;

        return BuildResult(
            terminalBalances,
            outcomes,
            slicesElapsed,
            maxDrawdowns,
            equityCurves,
            bestDayRatios,
            longestLosingStreaks,
            longestFlatStretches,
            ruinCauses,
            completed,
            expectedBlockSize,
            startingCapital,
            targetDurationSlices,
            drawdownType ?? "static",
            bestDayRulePercent,
            curveBytes,
            curvesRetained);
    }

    /// <summary>
    /// Builds the dispatch request for one dataset without running it, so a
    /// caller can hand many versions to <see cref="IComputeDispatcher.DispatchMonteCarloBatchAsync"/>
    /// as one unit of work (ROADMAP Phase 29B.1). Parameter derivation, the
    /// iteration clamp and the curve-retention ceiling are identical to
    /// <see cref="Run"/> — the only difference is that the work is not started.
    /// </summary>
    public MonteCarloDispatchRequest CreateRequest(
        TimeSlice[] slices,
        int targetDurationSlices,
        double startingCapital,
        string drawdownType,
        double maxDailyLossPercent,
        double maxLossPercent,
        double profitTargetPercent,
        int iterations = 1000,
        int expectedBlockSize = 5,
        double bestDayRulePercent = 50.0,
        int? seed = null,
        int maxDegreeOfParallelism = -1,
        bool retainCurves = true)
    {
        if (iterations < 1) iterations = 1;
        if (iterations > MaxIterations) iterations = MaxIterations;
        if (expectedBlockSize < 1) expectedBlockSize = 1;
        if (bestDayRulePercent < 0) bestDayRulePercent = 0;

        long curveCells = (long)iterations * targetDurationSlices;
        bool keepCurves = retainCurves && curveCells <= MaxCurveCells;

        int dailyFloorLoss = (int)Math.Round(startingCapital * (maxDailyLossPercent / 100.0));
        int maxFloorLoss = (int)Math.Round(startingCapital * (maxLossPercent / 100.0));
        int profitTarget = (int)Math.Round(startingCapital * (profitTargetPercent / 100.0));

        bool trailingEod = string.Equals(drawdownType, "trailing-eod", StringComparison.OrdinalIgnoreCase);
        bool trailingIntraday = string.Equals(drawdownType, "trailing-intraday", StringComparison.OrdinalIgnoreCase);

        var simulation = new MonteCarloSimulationRequest(
            TargetDurationSlices: targetDurationSlices,
            StartingCapital: startingCapital,
            DailyFloorLoss: dailyFloorLoss,
            MaxFloorLoss: maxFloorLoss,
            ProfitTarget: profitTarget,
            ExpectedBlockSize: expectedBlockSize,
            TrailingEod: trailingEod,
            TrailingIntraday: trailingIntraday);

        return new MonteCarloDispatchRequest(
            Simulation: simulation,
            Slices: slices,
            Iterations: iterations,
            Seed: seed,
            RetainCurves: keepCurves,
            MaxDegreeOfParallelism: maxDegreeOfParallelism);
    }

    /// <summary>
    /// Runs many Monte Carlo workloads as one dispatch (ROADMAP Phase 29B.1).
    /// Each request is built by <see cref="CreateRequest"/>; results are
    /// index-aligned with <paramref name="requests"/> and aggregated exactly as
    /// <see cref="Run"/> aggregates a single one. A cancelled batch returns a
    /// full-length array whose unfinished items carry an empty result.
    /// </summary>
    public MonteCarloResult[] RunBatch(
        IReadOnlyList<MonteCarloDispatchRequest> requests,
        CancellationToken cancellationToken = default)
    {
        if (requests == null || requests.Count == 0)
            return Array.Empty<MonteCarloResult>();

        var dispatched = _dispatcher
            .DispatchMonteCarloBatchAsync(requests, cancellationToken)
            .ConfigureAwait(false)
            .GetAwaiter()
            .GetResult();

        var results = new MonteCarloResult[requests.Count];
        for (int i = 0; i < requests.Count; i++)
            results[i] = BuildFromDispatch(requests[i], dispatched[i]);

        return results;
    }

    private MonteCarloResult BuildFromDispatch(
        MonteCarloDispatchRequest request,
        MonteCarloDispatchResult computed)
    {
        computed ??= MonteCarloDispatchResult.Empty;
        int completed = computed.Count;
        bool curvesRetained = computed.CurvesRetained;
        long curveBytes = curvesRetained
            ? (long)completed * request.Simulation.TargetDurationSlices * sizeof(double)
            : 0L;

        return BuildResult(
            computed.TerminalBalances,
            computed.Outcomes,
            computed.SlicesElapsed,
            computed.MaxDrawdowns,
            curvesRetained ? computed.EquityCurves : Array.Empty<double[]>(),
            computed.BestDayRatios,
            computed.LongestLosingStreaks,
            computed.LongestFlatStretches,
            computed.RuinCauses,
            completed,
            request.Simulation.ExpectedBlockSize,
            request.Simulation.StartingCapital,
            request.Simulation.TargetDurationSlices,
            request.Simulation.TrailingIntraday ? "trailing-intraday" : request.Simulation.TrailingEod ? "trailing-eod" : "static",
            0.0,
            curveBytes,
            curvesRetained);
    }

    private static MonteCarloResult BuildResult(
        double[] terminalBalances,
        byte[] outcomes,
        int[] slicesElapsed,
        double[] maxDrawdowns,
        double[][] equityCurves,
        double[] bestDayRatios,
        int[] longestLosingStreaks,
        int[] longestFlatStretches,
        byte[] ruinCauses,
        int iterations,
        int expectedBlockSize,
        double startingCapital,
        int targetDurationSlices,
        string drawdownType,
        double bestDayRulePercent,
        long curveMemoryBytes,
        bool curvesRetained)
    {
        int blown = 0, funded = 0, timeout = 0;
        double sumElapsedFund = 0;
        double sumElapsedRuin = 0;
        int ruinDaily = 0, ruinMax = 0;
        int bestDayViolations = 0;

        var fundedElapsed = new List<int>(iterations);
        var ruinedElapsed = new List<int>(iterations);

        double bestDayThreshold = bestDayRulePercent / 100.0;

        for (int i = 0; i < iterations; i++)
        {
            switch (outcomes[i])
            {
                case OutcomeBlown:
                    blown++;
                    sumElapsedRuin += slicesElapsed[i];
                    ruinedElapsed.Add(slicesElapsed[i]);

                    if (ruinCauses[i] == MonteCarloRuinCause.DailyFloor) ruinDaily++;
                    else if (ruinCauses[i] == MonteCarloRuinCause.MaxFloor) ruinMax++;
                    break;
                case OutcomeFunded:
                    funded++;
                    sumElapsedFund += slicesElapsed[i];
                    fundedElapsed.Add(slicesElapsed[i]);
                    break;
                default:
                    timeout++;
                    break;
            }

            if (bestDayThreshold > 0 && bestDayRatios[i] > bestDayThreshold)
                bestDayViolations++;
        }

        fundedElapsed.Sort();
        ruinedElapsed.Sort();

        var sortedTerminal = (double[])terminalBalances.Clone();
        Array.Sort(sortedTerminal);

        var sortedDd = (double[])maxDrawdowns.Clone();
        Array.Sort(sortedDd);

        var sortedRatios = (double[])bestDayRatios.Clone();
        Array.Sort(sortedRatios);

        var sortedLosing = (int[])longestLosingStreaks.Clone();
        Array.Sort(sortedLosing);

        var sortedFlat = (int[])longestFlatStretches.Clone();
        Array.Sort(sortedFlat);

        return new MonteCarloResult(
            TerminalBalances: terminalBalances,
            Outcomes: outcomes,
            SlicesElapsed: slicesElapsed,
            MaxDrawdowns: maxDrawdowns,
            EquityCurves: equityCurves,
            BestDayRatios: bestDayRatios,
            LongestLosingStreaks: longestLosingStreaks,
            LongestFlatStretches: longestFlatStretches,
            RuinCauses: ruinCauses,

            Iterations: iterations,
            ExpectedBlockSize: expectedBlockSize,
            StartingCapital: startingCapital,
            TargetDurationSlices: targetDurationSlices,
            DrawdownType: drawdownType,
            BestDayRulePercent: bestDayRulePercent,

            PRuin: iterations > 0 ? (double)blown / iterations : 0,
            PFunded: iterations > 0 ? (double)funded / iterations : 0,
            PTimeout: iterations > 0 ? (double)timeout / iterations : 0,
            ExpectedSlicesToFund: funded > 0 ? sumElapsedFund / funded : 0,
            ExpectedSlicesToRuin: blown > 0 ? sumElapsedRuin / blown : 0,
            MedianSlicesToFund: PercentileInt(fundedElapsed, 50),
            MedianSlicesToRuin: PercentileInt(ruinedElapsed, 50),

            TerminalBalanceMean: Mean(terminalBalances),
            TerminalBalanceMedian: Percentile(sortedTerminal, 50),
            TerminalBalanceStdDev: StdDev(terminalBalances),
            TerminalBalanceP05: Percentile(sortedTerminal, 5),
            TerminalBalanceP95: Percentile(sortedTerminal, 95),

            MaxDrawdownMean: Mean(maxDrawdowns),
            MaxDrawdownMedian: Percentile(sortedDd, 50),
            MaxDrawdownStdDev: StdDev(maxDrawdowns),
            MaxDrawdownP05: Percentile(sortedDd, 5),
            MaxDrawdownP95: Percentile(sortedDd, 95),

            ProbBestDayViolation: iterations > 0 ? (double)bestDayViolations / iterations : 0,
            BestDayRatioMedian: Percentile(sortedRatios, 50),
            BestDayRatioP95: Percentile(sortedRatios, 95),

            MedianLongestLosingStreak: PercentileInt(sortedLosing, 50),
            P95LongestLosingStreak: PercentileInt(sortedLosing, 95),
            MedianLongestFlatStretch: PercentileInt(sortedFlat, 50),
            P95LongestFlatStretch: PercentileInt(sortedFlat, 95),

            RuinByDailyFloorCount: ruinDaily,
            RuinByMaxFloorCount: ruinMax,

            EdgePercent: double.NaN,
            NullMeanTerminalBalance: double.NaN,
            NullIterations: 0,

            CurveMemoryBytes: curveMemoryBytes,
            CurvesRetained: curvesRetained);
    }

    private static MonteCarloResult EmptyResult(
        double startingCapital, int expectedBlockSize, int targetDurationSlices,
        string drawdownType, double bestDayRulePercent)
    {
        return new MonteCarloResult(
            TerminalBalances: Array.Empty<double>(),
            Outcomes: Array.Empty<byte>(),
            SlicesElapsed: Array.Empty<int>(),
            MaxDrawdowns: Array.Empty<double>(),
            EquityCurves: Array.Empty<double[]>(),
            BestDayRatios: Array.Empty<double>(),
            LongestLosingStreaks: Array.Empty<int>(),
            LongestFlatStretches: Array.Empty<int>(),
            RuinCauses: Array.Empty<byte>(),

            Iterations: 0,
            ExpectedBlockSize: expectedBlockSize,
            StartingCapital: startingCapital,
            TargetDurationSlices: targetDurationSlices,
            DrawdownType: drawdownType,
            BestDayRulePercent: bestDayRulePercent,

            PRuin: 0, PFunded: 0, PTimeout: 0,
            ExpectedSlicesToFund: 0, ExpectedSlicesToRuin: 0,
            MedianSlicesToFund: 0, MedianSlicesToRuin: 0,

            TerminalBalanceMean: 0, TerminalBalanceMedian: 0, TerminalBalanceStdDev: 0,
            TerminalBalanceP05: 0, TerminalBalanceP95: 0,

            MaxDrawdownMean: 0, MaxDrawdownMedian: 0, MaxDrawdownStdDev: 0,
            MaxDrawdownP05: 0, MaxDrawdownP95: 0,

            ProbBestDayViolation: 0, BestDayRatioMedian: 0, BestDayRatioP95: 0,

            MedianLongestLosingStreak: 0, P95LongestLosingStreak: 0,
            MedianLongestFlatStretch: 0, P95LongestFlatStretch: 0,

            RuinByDailyFloorCount: 0, RuinByMaxFloorCount: 0,

            EdgePercent: double.NaN,
            NullMeanTerminalBalance: double.NaN,
            NullIterations: 0,

            CurveMemoryBytes: 0,
            CurvesRetained: false);
    }

    private static double Mean(double[] values)
    {
        if (values.Length == 0) return 0;
        double sum = 0;
        for (int i = 0; i < values.Length; i++) sum += values[i];
        return sum / values.Length;
    }

    private static double StdDev(double[] values)
    {
        if (values.Length == 0) return 0;
        double mean = Mean(values);
        double sumSq = 0;
        for (int i = 0; i < values.Length; i++)
        {
            double d = values[i] - mean;
            sumSq += d * d;
        }
        return Math.Sqrt(sumSq / values.Length);
    }

    private static double Percentile(double[] sortedValues, double p)
    {
        if (sortedValues.Length == 0) return 0;
        if (sortedValues.Length == 1) return sortedValues[0];

        double rank = (p / 100.0) * (sortedValues.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];

        double weight = rank - lower;
        return sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight;
    }

    private static int PercentileInt(IReadOnlyList<int> sortedValues, double p)
    {
        if (sortedValues.Count == 0) return 0;
        if (sortedValues.Count == 1) return sortedValues[0];

        double rank = (p / 100.0) * (sortedValues.Count - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sortedValues[lower];

        double weight = rank - lower;
        return (int)Math.Round(sortedValues[lower] * (1 - weight) + sortedValues[upper] * weight);
    }
}
