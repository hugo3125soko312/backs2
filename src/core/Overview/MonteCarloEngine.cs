using System;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.Overview;

public sealed class MonteCarloEngine
{
    private const int OutcomeBlown = MonteCarloOutcome.Blown;
    private const int OutcomeFunded = MonteCarloOutcome.Funded;
    private const int OutcomeTimeout = MonteCarloOutcome.Timeout;

    public MonteCarloResult Run(
        TimeSlice[] slices,
        int targetDurationSlices,
        double startingCapital,
        string drawdownType,
        double maxDailyLossPercent,
        double maxLossPercent,
        double profitTargetPercent,
        int expectedBlockSize = 5,
        int? seed = null,
        IProgress<int>? progress = null)
    {
        if (slices == null || slices.Length == 0 || startingCapital <= 0 || targetDurationSlices <= 0)
        {
            return EmptyResult(startingCapital, expectedBlockSize, targetDurationSlices, drawdownType ?? "static");
        }

        if (expectedBlockSize < 1) expectedBlockSize = 1;

        int iterations = 1000;
        int n = slices.Length;

        var terminalBalances = new double[iterations];
        var outcomes = new byte[iterations];
        var slicesElapsed = new int[iterations];
        var maxDrawdowns = new double[iterations];

        int dailyFloorLoss = (int)Math.Round(startingCapital * (maxDailyLossPercent / 100.0));
        int maxFloorLoss = (int)Math.Round(startingCapital * (maxLossPercent / 100.0));
        int profitTarget = (int)Math.Round(startingCapital * (profitTargetPercent / 100.0));

        bool trailingEod = string.Equals(drawdownType, "trailing-eod", StringComparison.OrdinalIgnoreCase);
        bool trailingIntraday = string.Equals(drawdownType, "trailing-intraday", StringComparison.OrdinalIgnoreCase);

        int completed = 0;
        int progressStep = Math.Max(1, iterations / 20);

        var parallelOptions = new ParallelOptions
        {
            MaxDegreeOfParallelism = Environment.ProcessorCount
        };

        Parallel.For(0, iterations, parallelOptions, i =>
        {
            var rng = seed.HasValue ? new Random(seed.Value + i) : Random.Shared;

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
                out var terminal,
                out var outcome,
                out var elapsed,
                out var maxDd);

            terminalBalances[i] = terminal;
            outcomes[i] = outcome;
            slicesElapsed[i] = elapsed;
            maxDrawdowns[i] = maxDd;

            if (progress != null)
            {
                int current = Interlocked.Increment(ref completed);
                if (current % progressStep == 0 || current == iterations)
                    progress.Report(current);
            }
        });

        return BuildResult(
            terminalBalances,
            outcomes,
            slicesElapsed,
            maxDrawdowns,
            iterations,
            expectedBlockSize,
            startingCapital,
            targetDurationSlices,
            drawdownType ?? "static");
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
        out double terminalBalance,
        out byte outcome,
        out int slicesElapsed,
        out double maxDrawdownPercent)
    {
        double balance = startingCapital;
        double hwm = startingCapital;
        double dayStartBalance = startingCapital;
        double peak = startingCapital;
        double maxDd = 0;

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
                    // EOD reset: new trading day. Reset the daily floor anchor.
                    dayStartBalance = balance;

                    // trailing-eod updates HWM only at EOD; trailing-intraday updates continuously below.
                    if (trailingEod && balance > hwm) hwm = balance;
                }

                // --- 1. MTM-low check (intra-slice) ---
                double mtmBalance = balance + s.MtmLow;

                // Daily floor: relative to the day's start balance.
                double dailyFloor = dayStartBalance - dailyFloorLoss;

                // Max floor: relative to HWM for trailing, or fixed from initial for static.
                double maxFloor;
                if (trailingIntraday)
                    maxFloor = hwm - maxFloorLoss;
                else if (trailingEod)
                    maxFloor = hwm - maxFloorLoss;
                else
                    maxFloor = startingCapital - maxFloorLoss;

                if (mtmBalance <= dailyFloor || mtmBalance <= maxFloor)
                {
                    // Blown. Realize the worse of the two breaches so the terminal balance
                    // reflects the loss that killed the account.
                    double worst = Math.Min(dailyFloor, maxFloor);
                    balance = worst;
                    result = OutcomeBlown;
                    elapsed = produced + 1;
                    goto done;
                }

                // --- 2. Track peak on MTM high for drawdown measurement ---
                double mtmHigh = balance + s.MtmHigh;
                if (mtmHigh > peak) peak = mtmHigh;

                // --- 3. Realize PnL ---
                balance += s.RealizedPnl;

                // --- 4. Update HWM for trailing-intraday ---
                if (trailingIntraday && balance > hwm) hwm = balance;

                // --- 5. Track drawdown ---
                double dd = peak > 0 ? (peak - balance) / peak : 0;
                if (dd > maxDd) maxDd = dd;

                // --- 6. Profit target check (realized only) ---
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
        terminalBalance = balance;
        outcome = result;
        slicesElapsed = elapsed;
        maxDrawdownPercent = maxDd * 100.0;
    }

    private static MonteCarloResult BuildResult(
        double[] terminalBalances,
        byte[] outcomes,
        int[] slicesElapsed,
        double[] maxDrawdowns,
        int iterations,
        int expectedBlockSize,
        double startingCapital,
        int targetDurationSlices,
        string drawdownType)
    {
        int blown = 0, funded = 0, timeout = 0;
        double sumElapsedFund = 0;
        double sumElapsedRuin = 0;

        for (int i = 0; i < iterations; i++)
        {
            switch (outcomes[i])
            {
                case OutcomeBlown:
                    blown++;
                    sumElapsedRuin += slicesElapsed[i];
                    break;
                case OutcomeFunded:
                    funded++;
                    sumElapsedFund += slicesElapsed[i];
                    break;
                default:
                    timeout++;
                    break;
            }
        }

        var sortedTerminal = (double[])terminalBalances.Clone();
        Array.Sort(sortedTerminal);

        var sortedDd = (double[])maxDrawdowns.Clone();
        Array.Sort(sortedDd);

        return new MonteCarloResult(
            TerminalBalances: terminalBalances,
            Outcomes: outcomes,
            SlicesElapsed: slicesElapsed,
            MaxDrawdowns: maxDrawdowns,

            Iterations: iterations,
            ExpectedBlockSize: expectedBlockSize,
            StartingCapital: startingCapital,
            TargetDurationSlices: targetDurationSlices,
            DrawdownType: drawdownType,

            PRuin: iterations > 0 ? (double)blown / iterations : 0,
            PFunded: iterations > 0 ? (double)funded / iterations : 0,
            PTimeout: iterations > 0 ? (double)timeout / iterations : 0,
            ExpectedSlicesToFund: funded > 0 ? sumElapsedFund / funded : 0,
            ExpectedSlicesToRuin: blown > 0 ? sumElapsedRuin / blown : 0,

            TerminalBalanceMean: Mean(terminalBalances),
            TerminalBalanceMedian: Percentile(sortedTerminal, 50),
            TerminalBalanceStdDev: StdDev(terminalBalances),
            TerminalBalanceP05: Percentile(sortedTerminal, 5),
            TerminalBalanceP95: Percentile(sortedTerminal, 95),

            MaxDrawdownMean: Mean(maxDrawdowns),
            MaxDrawdownMedian: Percentile(sortedDd, 50),
            MaxDrawdownP05: Percentile(sortedDd, 5),
            MaxDrawdownP95: Percentile(sortedDd, 95));
    }

    private static MonteCarloResult EmptyResult(
        double startingCapital, int expectedBlockSize, int targetDurationSlices, string drawdownType)
    {
        return new MonteCarloResult(
            TerminalBalances: Array.Empty<double>(),
            Outcomes: Array.Empty<byte>(),
            SlicesElapsed: Array.Empty<int>(),
            MaxDrawdowns: Array.Empty<double>(),

            Iterations: 0,
            ExpectedBlockSize: expectedBlockSize,
            StartingCapital: startingCapital,
            TargetDurationSlices: targetDurationSlices,
            DrawdownType: drawdownType,

            PRuin: 0, PFunded: 0, PTimeout: 0,
            ExpectedSlicesToFund: 0, ExpectedSlicesToRuin: 0,

            TerminalBalanceMean: 0, TerminalBalanceMedian: 0, TerminalBalanceStdDev: 0,
            TerminalBalanceP05: 0, TerminalBalanceP95: 0,

            MaxDrawdownMean: 0, MaxDrawdownMedian: 0, MaxDrawdownP05: 0, MaxDrawdownP95: 0);
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
}