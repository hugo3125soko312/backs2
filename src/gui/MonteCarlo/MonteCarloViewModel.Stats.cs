using System;
using System.Collections.Generic;
using Avalonia.Media;
using backs2.Core.Overview;

namespace backs2.ViewModels;

public partial class MonteCarloViewModel
{
    private static readonly IBrush ToneNeutral = new SolidColorBrush(Color.Parse("#E0E0E0"));
    private static readonly IBrush ToneDim     = new SolidColorBrush(Color.Parse("#7A7A7A"));
    private static readonly IBrush ToneWarn    = new SolidColorBrush(Color.Parse("#EF5350"));
    private static readonly IBrush ToneGood    = new SolidColorBrush(Color.Parse("#26A69A"));
    private static readonly IBrush ToneAmber   = new SolidColorBrush(Color.Parse("#FFB74D"));
    private static readonly IBrush ToneAccent  = new SolidColorBrush(Color.Parse("#4FC3F7"));

    // Threshold below which Edge % is treated as indistinguishable from random.
    // Reads as "the strategy's directional skill is within noise of a coin flip."
    private const double EdgeNoiseBand = 5.0;

    private MonteCarloResult? _statsRenderedFrom;

    private void PopulateStats(MonteCarloResult r)
    {
        Kpis.Clear();
        Stats.Clear();

        Kpis.Add(new McKpiCard(
            "P(RUIN)",
            r.PRuin.ToString("P2"),
            $"of {r.Iterations:N0} simulations",
            r.PRuin > 0 ? ToneWarn : ToneGood));

        Kpis.Add(new McKpiCard(
            "P(FUNDED)",
            r.PFunded.ToString("P2"),
            "target reached",
            r.PFunded > 0 ? ToneGood : ToneDim));

        Kpis.Add(BuildEdgeKpi(r));

        Kpis.Add(new McKpiCard(
            "MEDIAN MAX DD",
            $"{r.MaxDrawdownMedian:0.00}%",
            "peak-to-trough",
            ToneAmber));

        Kpis.Add(new McKpiCard(
            "BEST-DAY VIOL",
            r.ProbBestDayViolation.ToString("P2"),
            $">{r.BestDayRulePercent:0.#}% rule",
            r.ProbBestDayViolation > 0.05 ? ToneWarn : ToneNeutral));

        Stats.Add(new McStatSection("TERMINAL BALANCE", new[]
        {
            new McStatLine("Mean",            r.TerminalBalanceMean.ToString("N0"),   ToneNeutral),
            new McStatLine("Median",          r.TerminalBalanceMedian.ToString("N0"), ToneNeutral),
            new McStatLine("Std Dev",         r.TerminalBalanceStdDev.ToString("N0"), ToneDim),
            new McStatLine("5th percentile",  r.TerminalBalanceP05.ToString("N0"),    ToneWarn),
            new McStatLine("95th percentile", r.TerminalBalanceP95.ToString("N0"),    ToneGood),
        }));

        Stats.Add(new McStatSection("TIME TO OUTCOME", new[]
        {
            new McStatLine("Median → Fund", $"{r.MedianSlicesToFund:N0} slices",   ToneGood),
            new McStatLine("Median → Ruin", $"{r.MedianSlicesToRuin:N0} slices",   ToneWarn),
            new McStatLine("Mean → Fund",   $"{r.ExpectedSlicesToFund:N0} slices", ToneDim),
            new McStatLine("Mean → Ruin",   $"{r.ExpectedSlicesToRuin:N0} slices", ToneDim),
        }));

        Stats.Add(new McStatSection("STREAKS", new[]
        {
            new McStatLine("Median losing streak",   $"{r.MedianLongestLosingStreak:N0} trades", ToneNeutral),
            new McStatLine("95th pct losing streak", $"{r.P95LongestLosingStreak:N0} trades",    ToneWarn),
            new McStatLine("Median flat stretch",    $"{r.MedianLongestFlatStretch:N0} slices",  ToneNeutral),
            new McStatLine("95th pct flat stretch",  $"{r.P95LongestFlatStretch:N0} slices",     ToneDim),
        }));

        if (!double.IsNaN(r.EdgePercent))
        {
            double delta = r.TerminalBalanceMean - r.NullMeanTerminalBalance;
            IBrush deltaTone = r.EdgePercent > EdgeNoiseBand  ? ToneGood
                             : r.EdgePercent < -EdgeNoiseBand ? ToneWarn
                             : ToneDim;

            Stats.Add(new McStatSection("EDGE VS RANDOM", new[]
            {
                new McStatLine("Strategy mean terminal", r.TerminalBalanceMean.ToString("N0"),  ToneNeutral),
                new McStatLine("Random-signal null mean", r.NullMeanTerminalBalance.ToString("N0"), ToneDim),
                new McStatLine("Delta", delta.ToString("+0;-0;0"), deltaTone),
                new McStatLine("Null iterations", r.NullIterations.ToString("N0"), ToneDim),
            }));
        }

        int ruinTotal = r.RuinByDailyFloorCount + r.RuinByMaxFloorCount;
        if (ruinTotal > 0)
        {
            double dailyPct = (double)r.RuinByDailyFloorCount / ruinTotal;
            double maxPct = (double)r.RuinByMaxFloorCount / ruinTotal;

            Stats.Add(new McStatSection("RUIN BREAKDOWN", new[]
            {
                new McStatLine("By daily floor",  $"{r.RuinByDailyFloorCount:N0}  ({dailyPct:P1})", ToneWarn),
                new McStatLine("By max-DD floor", $"{r.RuinByMaxFloorCount:N0}  ({maxPct:P1})",     ToneAccent),
            }));
        }

        if (!r.CurvesRetained && r.Iterations > 0)
        {
            Stats.Add(new McStatSection("MEMORY", new[]
            {
                new McStatLine("Equity curves", "dropped (memory ceiling)", ToneWarn),
                new McStatLine("Retention limit", $"{MonteCarloEngine.MaxCurveCells / 1_000_000:N0}M cells", ToneDim),
            }));
        }

        CurveMemoryLabel = r.CurvesRetained
            ? $"{r.Iterations:N0} sims × {r.TargetDurationSlices:N0} slices · {r.CurveMemoryBytes / (1024.0 * 1024.0):F1} MB"
            : $"{r.Iterations:N0} sims × {r.TargetDurationSlices:N0} slices · curves dropped";
    }

    private static McKpiCard BuildEdgeKpi(MonteCarloResult r)
    {
        if (double.IsNaN(r.EdgePercent))
        {
            return new McKpiCard(
                "EDGE %",
                "—",
                $"requires {EdgeMinTrades}+ trades",
                ToneDim);
        }

        string value = r.EdgePercent.ToString("+0.0;-0.0;0.0") + "%";

        if (r.EdgePercent > EdgeNoiseBand)
        {
            return new McKpiCard(
                "EDGE %",
                value,
                $"vs null ({r.NullIterations:N0} sims)",
                ToneGood);
        }

        if (r.EdgePercent < -EdgeNoiseBand)
        {
            return new McKpiCard(
                "EDGE %",
                value,
                $"worse than random ({r.NullIterations:N0} sims)",
                ToneWarn);
        }

        return new McKpiCard(
            "EDGE %",
            value,
            "indistinguishable from random",
            ToneDim);
    }
}