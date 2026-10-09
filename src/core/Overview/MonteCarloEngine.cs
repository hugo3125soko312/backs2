using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

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

        int n = slices.Length;

        long curveCells = (long)iterations * targetDurationSlices;
        bool keepCurves = retainCurves && curveCells <= MaxCurveCells;

        var terminalBalances = new double[iterations];
        var outcomes = new byte[iterations];
        var slicesElapsed = new int[iterations];
        var maxDrawdowns = new double[iterations];
        var equityCurves = keepCurves ? new double[iterations][] : Array.Empty<double[]>();
        var bestDayRatios = new double[iterations];
        var longestLosingStreaks = new int[iterations];
        var longestFlatStretches = new int[iterations];
        var ruinCauses = new byte[iterations];

        int dailyFloorLoss = (int)Math.Round(startingCapital * (maxDailyLossPercent / 100.0));
        int maxFloorLoss = (int)Math.Round(startingCapital * (maxLossPercent / 100.0));
        int profitTarget = (int)Math.Round(startingCapital * (profitTargetPercent / 100.0));

        bool trailingEod = string.Equals(drawdownType, "trailing-eod", StringComparison.OrdinalIgnoreCase);
        bool trailingIntraday = string.Equals(drawdownType, "trailing-intraday", StringComparison.OrdinalIgnoreCase);

        int completed = 0;
        int progressStep = Math.Max(1, iterations / 20);

        // Normalize the DOP:
        //   1       -> sequential fast-path
        //   >1      -> exact thread count
        //   <=0 (-1) -> Environment.ProcessorCount (current default)
        int dop = maxDegreeOfParallelism == 1
            ? 1
            : maxDegreeOfParallelism > 1
                ? maxDegreeOfParallelism
                : Environment.ProcessorCount;

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = dop,
            CancellationToken = cancellationToken
        };

        // Shared iteration body. Captured by both the sequential loop and
        // the Parallel.For below. Local function so the body lives once.
        void RunIteration(int i)
        {
            var rng = seed.HasValue ? new Random(seed.Value + i) : Random.Shared;

            double[]? curve = keepCurves ? new double[targetDurationSlices] : null;

            SimulateOnce(
                slices,
                n,
                targetDurationSlices,
                startingCapital,
                dailyFloorLoss,
                maxFloorLoss,
                profitTarget,
                trailingEod,
                trailingIntraday,
                expectedBlockSize,
                rng,
                curve,
                out var terminal,
                out var outcome,
                out var elapsed,
                out var maxDd,
                out var bestDayRatio,
                out var longestLosing,
                out var longestFlat,
                out var ruinCause);

            terminalBalances[i] = terminal;
            outcomes[i] = outcome;
            slicesElapsed[i] = elapsed;
            maxDrawdowns[i] = maxDd;
            if (keepCurves) equityCurves[i] = curve!;
            bestDayRatios[i] = bestDayRatio;
            longestLosingStreaks[i] = longestLosing;
            longestFlatStretches[i] = longestFlat;
            ruinCauses[i] = ruinCause;

            if (progress != null)
            {
                int current = Interlocked.Increment(ref completed);
                if (current % progressStep == 0 || current == iterations)
                    progress.Report(current);
            }
        }

        try
        {
            if (dop == 1)
            {
                for (int i = 0; i < iterations; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    RunIteration(i);
                }
            }
            else
            {
                Parallel.For(0, iterations, parallelOptions, RunIteration);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }

        long curveBytes = keepCurves
            ? (long)iterations * targetDurationSlices * sizeof(double)
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
            iterations,
            expectedBlockSize,
            startingCapital,
            targetDurationSlices,
            drawdownType ?? "static",
            bestDayRulePercent,
            curveBytes,
            keepCurves);
    }

    private static void SimulateOnce(
        TimeSlice[] slices,
        int n,
        int targetDurationSlices,
        double startingCapital,
        int dailyFloorLoss,
        int maxFloorLoss,
        int profitTarget,
        bool trailingEod,
        bool trailingIntraday,
        int expectedBlockSize,
        Random rng,
        double[]? curve,
        out double terminalBalance,
        out byte outcome,
        out int slicesElapsed,
        out double maxDrawdownPercent,
        out double bestDayRatio,
        out int longestLosingStreak,
        out int longestFlatStretch,
        out byte ruinCause)
    {
        double balance = startingCapital;
        double hwm = startingCapital;
        double dayStartBalance = startingCapital;
        double peak = startingCapital;
        double maxDd = 0;

        double dayRealized = 0;
        double maxDayRealized = 0;

        int losingStreak = 0;
        int longestLosing = 0;

        int flatStretch = 0;
        int longestFlat = 0;

        byte cause = MonteCarloRuinCause.None;

        int elapsed = 0;
        byte result = OutcomeTimeout;

        int produced = 0;
        double p = 1.0 / expectedBlockSize;

        while (produced < targetDurationSlices)
        {
            int start = rng.Next(n);

            int blockLen = 1;
            while (rng.NextDouble() > p && blockLen < n)
                blockLen++;

            for (int k = 0; k < blockLen && produced < targetDurationSlices; k++, produced++)
            {
                var s = slices[(start + k) % n];

                if (s.IsEod)
                {
                    if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;
                    dayRealized = 0;

                    dayStartBalance = balance;

                    if (trailingEod && balance > hwm) hwm = balance;
                }

                double mtmBalance = balance + s.MtmLow;

                double dailyFloor = dayStartBalance - dailyFloorLoss;

                double maxFloor;
                if (trailingIntraday)
                    maxFloor = hwm - maxFloorLoss;
                else if (trailingEod)
                    maxFloor = hwm - maxFloorLoss;
                else
                    maxFloor = startingCapital - maxFloorLoss;

                bool dailyHit = mtmBalance <= dailyFloor;
                bool maxHit = mtmBalance <= maxFloor;

                if (dailyHit || maxHit)
                {
                    double worst = Math.Min(dailyFloor, maxFloor);
                    balance = worst;
                    if (curve != null) curve[produced] = balance;
                    result = OutcomeBlown;
                    elapsed = produced + 1;

                    if (dailyHit && maxHit)
                        cause = dailyFloor >= maxFloor ? MonteCarloRuinCause.DailyFloor : MonteCarloRuinCause.MaxFloor;
                    else if (dailyHit)
                        cause = MonteCarloRuinCause.DailyFloor;
                    else
                        cause = MonteCarloRuinCause.MaxFloor;

                    goto done;
                }

                double peakBefore = peak;
                double mtmHigh = balance + s.MtmHigh;
                if (mtmHigh > peak) peak = mtmHigh;

                balance += s.RealizedPnl;
                dayRealized += s.RealizedPnl;

                if (s.RealizedPnl < 0)
                {
                    losingStreak++;
                    if (losingStreak > longestLosing) longestLosing = losingStreak;
                }
                else if (s.RealizedPnl > 0)
                {
                    losingStreak = 0;
                }

                if (peak > peakBefore)
                    flatStretch = 0;
                else
                    flatStretch++;
                if (flatStretch > longestFlat) longestFlat = flatStretch;

                if (trailingIntraday && balance > hwm) hwm = balance;

                double dd = peak > 0 ? (peak - balance) / peak : 0;
                if (dd > maxDd) maxDd = dd;

                if (curve != null) curve[produced] = balance;

                if (profitTarget > 0 && balance - startingCapital >= profitTarget)
                {
                    result = OutcomeFunded;
                    elapsed = produced + 1;
                    goto done;
                }
            }
        }

        elapsed = targetDurationSlices;

    done:
        if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;

        terminalBalance = balance;
        outcome = result;
        slicesElapsed = elapsed;
        maxDrawdownPercent = maxDd * 100.0;

        double profit = terminalBalance - startingCapital;
        bestDayRatio = (profit > 0 && maxDayRealized > 0) ? maxDayRealized / profit : 0.0;

        longestLosingStreak = longestLosing;
        longestFlatStretch = longestFlat;
        ruinCause = cause;
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