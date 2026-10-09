using System;

namespace backs2.Core.Optimizer;

/// <summary>One parameter name/value/type tuple. Part of a version's grid coordinate.</summary>
public sealed record ParameterSet(
    string Name,
    string Type,
    string Value);

/// <summary>
/// Scalar Monte Carlo summary retained per version. Subset of
/// MonteCarloResult — per-simulation arrays are not stored here.
/// The only arrays that survive to disk live on OptimizationVersion.
/// </summary>
public sealed record McSummary(
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
    double MaxDrawdownP95,
    double ProbBestDayViolation,
    double BestDayRatioMedian,
    double BestDayRatioP95,
    int MedianLongestLosingStreak,
    int P95LongestLosingStreak,
    int MedianLongestFlatStretch,
    int P95LongestFlatStretch,
    int RuinByDailyFloorCount,
    int RuinByMaxFloorCount,
    double EdgePercent,
    double NullMeanTerminalBalance,
    int NullIterations);

/// <summary>
/// One parameter combination in an optimization batch: its grid coordinate,
/// its backtest result scalars, and — when the MC pass ran — the MC scalars,
/// terminal-balance array, and a downsampled representative equity curve.
///
/// NetPnl is the backtest-level sum of trade PnL (price units, position size
/// 1.0) — it is not MC-derived. Set at backtest time and preserved across
/// the MC refine pass.
///
/// RepresentativeCurve is the simulation closest to the median terminal
/// balance, resampled to ~1000 evenly-spaced points. Empty when the MC pass
/// dropped curves (CurvesRetained = false).
/// </summary>
public sealed record OptimizationVersion(
    int Index,
    ParameterSet[] Parameters,
    bool BacktestSuccess,
    string? BacktestError,
    long BacktestDurationMs,
    int TradeCount,
    double NetPnl,
    bool McRan,
    McSummary? Mc,
    double[] TerminalBalances,
    double[] RepresentativeCurve,
    bool CurvesRetained);

/// <summary>
/// A full optimization batch — one search over one strategy on one dataset,
/// stored at data/optimizations/batch_{Id}.json. Id is the UTC timestamp
/// "yyyyMMdd-HHmmss" the batch was started.
///
/// DataSpanYears is the historical span of the backtest dataset, in years.
/// Used to annualize the backtest-level NetPnl into a backtest CAGR. Zero
/// when the dataset was too short to yield a meaningful span.
/// </summary>
public sealed record OptimizationBatch(
    string Id,
    string StrategyName,
    string StrategyPath,
    string Symbol,
    string Timeframe,
    DateTime RunUtc,
    int CoarseIterations,
    int RefineIterations,
    int RefineTopN,
    double StartingCapital,
    string DrawdownType,
    double MaxLossPercent,
    double MaxDailyLossPercent,
    double ProfitTargetPercent,
    double BestDayRulePercent,
    int CombinationCount,
    double DataSpanYears,
    OptimizationVersion[] Versions);