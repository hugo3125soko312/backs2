namespace backs2.Core.Overview;

public static class MonteCarloOutcome
{
    public const byte Blown = 0;
    public const byte Funded = 1;
    public const byte Timeout = 2;
}

public sealed record MonteCarloResult(
    double[] TerminalBalances,
    byte[] Outcomes,
    int[] SlicesElapsed,
    double[] MaxDrawdowns,

    int Iterations,
    int ExpectedBlockSize,
    double StartingCapital,
    int TargetDurationSlices,
    string DrawdownType,

    double PRuin,
    double PFunded,
    double PTimeout,
    double ExpectedSlicesToFund,
    double ExpectedSlicesToRuin,

    double TerminalBalanceMean,
    double TerminalBalanceMedian,
    double TerminalBalanceStdDev,
    double TerminalBalanceP05,
    double TerminalBalanceP95,

    double MaxDrawdownMean,
    double MaxDrawdownMedian,
    double MaxDrawdownP05,
    double MaxDrawdownP95
);