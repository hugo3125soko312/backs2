namespace backs2.Core.Overview;

public static class MonteCarloOutcome
{
    public const byte Blown = 0;
    public const byte Funded = 1;
    public const byte Timeout = 2;
}

public static class MonteCarloRuinCause
{
    public const byte None = 0;
    public const byte DailyFloor = 1;
    public const byte MaxFloor = 2;
}

public sealed record MonteCarloResult(
    // Per-simulation arrays (parallel, length = Iterations)
    double[] TerminalBalances,
    byte[] Outcomes,
    int[] SlicesElapsed,
    double[] MaxDrawdowns,
    double[][] EquityCurves,
    double[] BestDayRatios,
    int[] LongestLosingStreaks,
    int[] LongestFlatStretches,
    byte[] RuinCauses,

    // Configuration echo
    int Iterations,
    int ExpectedBlockSize,
    double StartingCapital,
    int TargetDurationSlices,
    string DrawdownType,
    double BestDayRulePercent,

    // Outcome probabilities
    double PRuin,
    double PFunded,
    double PTimeout,
    double ExpectedSlicesToFund,
    double ExpectedSlicesToRuin,
    double MedianSlicesToFund,
    double MedianSlicesToRuin,

    // Terminal balance
    double TerminalBalanceMean,
    double TerminalBalanceMedian,
    double TerminalBalanceStdDev,
    double TerminalBalanceP05,
    double TerminalBalanceP95,

    // Max drawdown
    double MaxDrawdownMean,
    double MaxDrawdownMedian,
    double MaxDrawdownStdDev,
    double MaxDrawdownP05,
    double MaxDrawdownP95,

    // Best-day rule
    double ProbBestDayViolation,
    double BestDayRatioMedian,
    double BestDayRatioP95,

    // Streaks
    int MedianLongestLosingStreak,
    int P95LongestLosingStreak,
    int MedianLongestFlatStretch,
    int P95LongestFlatStretch,

    // Ruin cause breakdown
    int RuinByDailyFloorCount,
    int RuinByMaxFloorCount,

    long CurveMemoryBytes
);