using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using backs2.Core.Runner;

namespace backs2.Core.Overview;

public sealed class MonteCarloEngine
{
    private const double TradingDaysPerYear = 252.0;

    public MonteCarloResult Run(
        IReadOnlyList<Trade> trades,
        TimeSpan backtestDuration,
        int iterations = 1000,
        int meanBlockSize = 5,
        double startingCapital = 10000,
        double ruinThresholdPercent = 50,
        int? seed = null,
        IProgress<int>? progress = null)
    {
        if (trades == null || trades.Count < 2)
        {
            return EmptyResult(iterations, meanBlockSize, startingCapital, ruinThresholdPercent);
        }

        if (iterations < 1) iterations = 1;
        if (meanBlockSize < 1) meanBlockSize = 1;

        double backtestYears = Math.Max(backtestDuration.TotalDays / 365.25, 1.0 / 365.25);

        var netProfits = new double[iterations];
        var maxDrawdowns = new double[iterations];
        var annualReturns = new double[iterations];
        var sharpes = new double[iterations];
        var profitFactors = new double[iterations];
        var maxConsecutiveLosses = new int[iterations];

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
                trades,
                meanBlockSize,
                startingCapital,
                backtestYears,
                rng,
                out var finalEquity,
                out var maxDD,
                out var maxCL,
                out var grossProfit,
                out var grossLoss,
                out var sumReturns,
                out var sumReturnsSq,
                out var tradeCount);

            netProfits[i] = finalEquity;
            maxDrawdowns[i] = maxDD;
            maxConsecutiveLosses[i] = maxCL;

            annualReturns[i] = backtestYears > 0
                ? (Math.Pow(Math.Max(finalEquity, 1e-9) / startingCapital, 1.0 / backtestYears) - 1.0) * 100.0
                : 0.0;

            double pf = grossLoss > 0 ? grossProfit / grossLoss : (grossProfit > 0 ? 999.0 : 0.0);
            profitFactors[i] = pf;

            sharpes[i] = ComputeSharpe(sumReturns, sumReturnsSq, tradeCount);

            if (progress != null)
            {
                int current = Interlocked.Increment(ref completed);
                if (current % progressStep == 0 || current == iterations)
                    progress.Report(current);
            }
        });

        return BuildResult(
            netProfits,
            maxDrawdowns,
            annualReturns,
            sharpes,
            profitFactors,
            maxConsecutiveLosses,
            iterations,
            meanBlockSize,
            startingCapital,
            ruinThresholdPercent,
            backtestYears);
    }

    private static void SimulateOnce(
        IReadOnlyList<Trade> trades,
        int meanBlockSize,
        double startingCapital,
        double backtestYears,
        Random rng,
        out double finalEquity,
        out double maxDrawdownPercent,
        out int maxConsecutiveLosses,
        out double grossProfit,
        out double grossLoss,
        out double sumReturns,
        out double sumReturnsSq,
        out int tradeCount)
    {
        int n = trades.Count;
        double equity = startingCapital;
        double peak = startingCapital;
        double maxDD = 0;
        int consecutiveLosses = 0;
        int maxCL = 0;

        grossProfit = 0;
        grossLoss = 0;
        sumReturns = 0;
        sumReturnsSq = 0;
        tradeCount = 0;

        int produced = 0;
        double p = 1.0 / meanBlockSize;

        while (produced < n)
        {
            int start = rng.Next(n);

            int blockLen = 1;
            while (rng.NextDouble() > p && blockLen < n)
                blockLen++;

            int remaining = n - produced;
            if (blockLen > remaining) blockLen = remaining;

            for (int k = 0; k < blockLen; k++)
            {
                var trade = trades[(start + k) % n];

                double r = trade.PnLPercent;
                equity *= (1.0 + r);

                sumReturns += r;
                sumReturnsSq += r * r;
                tradeCount++;

                if (trade.PnL > 0) grossProfit += trade.PnL;
                else if (trade.PnL < 0) grossLoss += -trade.PnL;

                if (equity > peak) peak = equity;
                double dd = peak > 0 ? (peak - equity) / peak : 0;
                if (dd > maxDD) maxDD = dd;

                if (trade.PnL < 0)
                {
                    consecutiveLosses++;
                    if (consecutiveLosses > maxCL) maxCL = consecutiveLosses;
                }
                else
                {
                    consecutiveLosses = 0;
                }
            }

            produced += blockLen;
        }

        finalEquity = equity;
        maxDrawdownPercent = maxDD * 100.0;
        maxConsecutiveLosses = maxCL;
    }

    private static double ComputeSharpe(double sumReturns, double sumReturnsSq, int n)
    {
        if (n < 2) return 0;
        double mean = sumReturns / n;
        double variance = (sumReturnsSq / n) - (mean * mean);
        if (variance <= 0) return 0;
        double stdev = Math.Sqrt(variance);
        if (stdev <= 0) return 0;
        return (mean / stdev) * Math.Sqrt(TradingDaysPerYear);
    }

    private static MonteCarloResult BuildResult(
        double[] netProfits,
        double[] maxDrawdowns,
        double[] annualReturns,
        double[] sharpes,
        double[] profitFactors,
        int[] maxConsecutiveLosses,
        int iterations,
        int meanBlockSize,
        double startingCapital,
        double ruinThresholdPercent,
        double backtestYears)
    {
        var sortedNetProfits = (double[])netProfits.Clone();
        Array.Sort(sortedNetProfits);

        var sortedDrawdowns = (double[])maxDrawdowns.Clone();
        Array.Sort(sortedDrawdowns);

        var sortedAnnual = (double[])annualReturns.Clone();
        Array.Sort(sortedAnnual);

        var sortedSharpes = (double[])sharpes.Clone();
        Array.Sort(sortedSharpes);

        var sortedPfs = (double[])profitFactors.Clone();
        Array.Sort(sortedPfs);

        double ruinFloor = startingCapital * (1.0 - ruinThresholdPercent / 100.0);
        int ruinCount = 0;
        for (int i = 0; i < iterations; i++)
            if (netProfits[i] < ruinFloor) ruinCount++;

        double clSum = 0;
        int clMax = 0;
        for (int i = 0; i < iterations; i++)
        {
            clSum += maxConsecutiveLosses[i];
            if (maxConsecutiveLosses[i] > clMax) clMax = maxConsecutiveLosses[i];
        }

        return new MonteCarloResult(
            NetProfits: netProfits,
            MaxDrawdowns: maxDrawdowns,
            AnnualReturns: annualReturns,
            Sharpes: sharpes,
            ProfitFactors: profitFactors,
            MaxConsecutiveLosses: maxConsecutiveLosses,
            Iterations: iterations,
            MeanBlockSize: meanBlockSize,
            StartingCapital: startingCapital,
            RuinThresholdPercent: ruinThresholdPercent,
            BacktestYears: backtestYears,
            RiskOfRuin: iterations > 0 ? (double)ruinCount / iterations : 0,
            NetProfitMean: Mean(netProfits),
            NetProfitMedian: Percentile(sortedNetProfits, 50),
            NetProfitStdDev: StdDev(netProfits),
            NetProfitP05: Percentile(sortedNetProfits, 5),
            NetProfitP95: Percentile(sortedNetProfits, 95),
            MaxDrawdownMean: Mean(maxDrawdowns),
            MaxDrawdownMedian: Percentile(sortedDrawdowns, 50),
            MaxDrawdownP05: Percentile(sortedDrawdowns, 5),
            MaxDrawdownP95: Percentile(sortedDrawdowns, 95),
            AnnualReturnMean: Mean(annualReturns),
            AnnualReturnP05: Percentile(sortedAnnual, 5),
            AnnualReturnP95: Percentile(sortedAnnual, 95),
            SharpeMean: Mean(sharpes),
            SharpeP05: Percentile(sortedSharpes, 5),
            SharpeP95: Percentile(sortedSharpes, 95),
            ProfitFactorMean: Mean(profitFactors),
            ProfitFactorP05: Percentile(sortedPfs, 5),
            ProfitFactorP95: Percentile(sortedPfs, 95),
            MaxConsecutiveLossesMax: clMax,
            MaxConsecutiveLossesMean: iterations > 0 ? clSum / iterations : 0);
    }

    private static MonteCarloResult EmptyResult(
        int iterations, int meanBlockSize, double startingCapital, double ruinThresholdPercent)
    {
        return new MonteCarloResult(
            NetProfits: Array.Empty<double>(),
            MaxDrawdowns: Array.Empty<double>(),
            AnnualReturns: Array.Empty<double>(),
            Sharpes: Array.Empty<double>(),
            ProfitFactors: Array.Empty<double>(),
            MaxConsecutiveLosses: Array.Empty<int>(),
            Iterations: iterations,
            MeanBlockSize: meanBlockSize,
            StartingCapital: startingCapital,
            RuinThresholdPercent: ruinThresholdPercent,
            BacktestYears: 0,
            RiskOfRuin: 0,
            NetProfitMean: 0, NetProfitMedian: 0, NetProfitStdDev: 0, NetProfitP05: 0, NetProfitP95: 0,
            MaxDrawdownMean: 0, MaxDrawdownMedian: 0, MaxDrawdownP05: 0, MaxDrawdownP95: 0,
            AnnualReturnMean: 0, AnnualReturnP05: 0, AnnualReturnP95: 0,
            SharpeMean: 0, SharpeP05: 0, SharpeP95: 0,
            ProfitFactorMean: 0, ProfitFactorP05: 0, ProfitFactorP95: 0,
            MaxConsecutiveLossesMax: 0, MaxConsecutiveLossesMean: 0);
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