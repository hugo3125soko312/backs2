namespace backs2.Core.Overview;

public sealed record MonteCarloResult(
    double[] NetProfits,
    double[] MaxDrawdowns,
    double[] AnnualReturns,
    double[] Sharpes,
    double[] ProfitFactors,
    int[] MaxConsecutiveLosses,

    int Iterations,
    int MeanBlockSize,
    double StartingCapital,
    double RuinThresholdPercent,
    double BacktestYears,

    double RiskOfRuin,

    double NetProfitMean,
    double NetProfitMedian,
    double NetProfitStdDev,
    double NetProfitP05,
    double NetProfitP95,

    double MaxDrawdownMean,
    double MaxDrawdownMedian,
    double MaxDrawdownP05,
    double MaxDrawdownP95,

    double AnnualReturnMean,
    double AnnualReturnP05,
    double AnnualReturnP95,

    double SharpeMean,
    double SharpeP05,
    double SharpeP95,

    double ProfitFactorMean,
    double ProfitFactorP05,
    double ProfitFactorP95,

    int MaxConsecutiveLossesMax,
    double MaxConsecutiveLossesMean
);