using System;
using System.Globalization;
using System.Text;
using backs2.Core.Optimizer;

namespace backs2.ViewModels;

public sealed class OptimizationResultRow
{
    public string BatchId { get; }
    public string BatchFile { get; }
    public int VersionIndex { get; }
    public string StrategyName { get; }
    public string Symbol { get; }
    public string Timeframe { get; }
    public string ParameterSummary { get; }
    public int TradeCount { get; }
    public long BacktestDurationMs { get; }
    public bool McRan { get; }
    public McSummary? Mc { get; }

    /// <summary>Backtest-level sum of trade PnL. Price units (size = 1.0).</summary>
    public double NetPnl { get; }

    /// <summary>
    /// Backtest CAGR: (1 + NetPnl / StartingCapital)^(1 / DataSpanYears) - 1.
    /// NaN when the batch predates the field (DataSpanYears = 0) or when the
    /// post-PnL ratio would be <= 0 (bankruptcy in the backtest span).
    /// </summary>
    public double Cagr { get; }

    public OptimizationResultRow(
        string batchId,
        string batchFile,
        OptimizationVersion version,
        OptimizationBatch batch)
    {
        BatchId = batchId;
        BatchFile = batchFile;
        VersionIndex = version.Index;
        StrategyName = batch.StrategyName;
        Symbol = batch.Symbol;
        Timeframe = batch.Timeframe;
        ParameterSummary = FormatParams(version.Parameters);
        TradeCount = version.TradeCount;
        BacktestDurationMs = version.BacktestDurationMs;
        McRan = version.McRan;
        Mc = version.Mc;

        NetPnl = version.NetPnl;

        if (batch.DataSpanYears > 0.0 && batch.StartingCapital > 0.0)
        {
            double ratio = 1.0 + NetPnl / batch.StartingCapital;
            Cagr = ratio > 0.0
                ? Math.Pow(ratio, 1.0 / batch.DataSpanYears) - 1.0
                : double.NaN;
        }
        else
        {
            Cagr = double.NaN;
        }
    }

    public string Dataset => $"{Symbol}@{Timeframe}";

    public string EdgeDisplay     => Mc == null ? "-" : $"{Mc.EdgePercent:F2}%";
    public string PFundedDisplay  => Mc == null ? "-" : $"{Mc.PFunded * 100:F1}%";
    public string PRuinDisplay    => Mc == null ? "-" : $"{Mc.PRuin * 100:F1}%";
    public string PTimeoutDisplay => Mc == null ? "-" : $"{Mc.PTimeout * 100:F1}%";

    public string MedianBalanceDisplay
        => Mc == null ? "-" : Mc.TerminalBalanceMedian.ToString("F2", CultureInfo.InvariantCulture);

    public string MaxDdMedianDisplay
        => Mc == null ? "-" : Mc.MaxDrawdownMedian.ToString("F2", CultureInfo.InvariantCulture);

    public string BestDayViolationDisplay
        => Mc == null ? "-" : $"{Mc.ProbBestDayViolation * 100:F1}%";

    public string NetPnlDisplay
        => NetPnl.ToString("F2", CultureInfo.InvariantCulture);

    public string CagrDisplay
        => double.IsNaN(Cagr) ? "-" : $"{Cagr * 100:F2}%";

    public static readonly string[] AvailableSortKeys =
    {
        "EdgePercent", "PFunded", "PRuin", "PTimeout",
        "NetPnl", "Cagr",
        "ExpectedSlicesToFund", "ExpectedSlicesToRuin",
        "TerminalBalanceMean", "TerminalBalanceMedian", "TerminalBalanceStdDev",
        "TerminalBalanceP05", "TerminalBalanceP95",
        "MaxDrawdownMean", "MaxDrawdownMedian", "MaxDrawdownP05", "MaxDrawdownP95",
        "ProbBestDayViolation", "BestDayRatioMedian", "BestDayRatioP95",
        "MedianLongestLosingStreak", "P95LongestLosingStreak",
        "MedianLongestFlatStretch", "P95LongestFlatStretch",
        "RuinByDailyFloorCount", "RuinByMaxFloorCount",
        "NullMeanTerminalBalance", "NullIterations",
        "TradeCount", "BacktestDurationMs"
    };

    public double SortValue(string key)
    {
        if (key == "TradeCount") return TradeCount;
        if (key == "BacktestDurationMs") return BacktestDurationMs;
        if (key == "NetPnl") return NetPnl;
        if (key == "Cagr") return double.IsNaN(Cagr) ? double.NegativeInfinity : Cagr;

        if (Mc == null) return double.NegativeInfinity;

        return key switch
        {
            "EdgePercent" => Mc.EdgePercent,
            "PFunded" => Mc.PFunded,
            "PRuin" => Mc.PRuin,
            "PTimeout" => Mc.PTimeout,
            "ExpectedSlicesToFund" => Mc.ExpectedSlicesToFund,
            "ExpectedSlicesToRuin" => Mc.ExpectedSlicesToRuin,
            "TerminalBalanceMean" => Mc.TerminalBalanceMean,
            "TerminalBalanceMedian" => Mc.TerminalBalanceMedian,
            "TerminalBalanceStdDev" => Mc.TerminalBalanceStdDev,
            "TerminalBalanceP05" => Mc.TerminalBalanceP05,
            "TerminalBalanceP95" => Mc.TerminalBalanceP95,
            "MaxDrawdownMean" => Mc.MaxDrawdownMean,
            "MaxDrawdownMedian" => Mc.MaxDrawdownMedian,
            "MaxDrawdownP05" => Mc.MaxDrawdownP05,
            "MaxDrawdownP95" => Mc.MaxDrawdownP95,
            "ProbBestDayViolation" => Mc.ProbBestDayViolation,
            "BestDayRatioMedian" => Mc.BestDayRatioMedian,
            "BestDayRatioP95" => Mc.BestDayRatioP95,
            "MedianLongestLosingStreak" => Mc.MedianLongestLosingStreak,
            "P95LongestLosingStreak" => Mc.P95LongestLosingStreak,
            "MedianLongestFlatStretch" => Mc.MedianLongestFlatStretch,
            "P95LongestFlatStretch" => Mc.P95LongestFlatStretch,
            "RuinByDailyFloorCount" => Mc.RuinByDailyFloorCount,
            "RuinByMaxFloorCount" => Mc.RuinByMaxFloorCount,
            "NullMeanTerminalBalance" => Mc.NullMeanTerminalBalance,
            "NullIterations" => Mc.NullIterations,
            _ => double.NegativeInfinity
        };
    }

    public static bool IsLowerBetter(string key) => key switch
    {
        "PRuin" => true,
        "PTimeout" => true,
        "ExpectedSlicesToRuin" => true,
        "TerminalBalanceStdDev" => true,
        "MaxDrawdownMean" => true,
        "MaxDrawdownMedian" => true,
        "MaxDrawdownP05" => true,
        "MaxDrawdownP95" => true,
        "ProbBestDayViolation" => true,
        "BestDayRatioMedian" => true,
        "BestDayRatioP95" => true,
        "MedianLongestLosingStreak" => true,
        "P95LongestLosingStreak" => true,
        "MedianLongestFlatStretch" => true,
        "P95LongestFlatStretch" => true,
        "RuinByDailyFloorCount" => true,
        "RuinByMaxFloorCount" => true,
        "BacktestDurationMs" => true,
        _ => false
    };

    private static string FormatParams(ParameterSet[] ps)
    {
        if (ps == null || ps.Length == 0) return "";
        var sb = new StringBuilder(ps.Length * 16);
        for (int i = 0; i < ps.Length; i++)
        {
            if (i > 0) sb.Append(", ");
            sb.Append(ps[i].Name).Append('=').Append(ps[i].Value);
        }
        return sb.ToString();
    }
}