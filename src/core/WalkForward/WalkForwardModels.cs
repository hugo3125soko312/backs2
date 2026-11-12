using System;
using backs2.Core.Optimizer;

namespace backs2.Core.WalkForward;

/// <summary>
/// String constants for the phase labels carried on <see cref="WalkForwardProgress"/>.
/// The values are user-visible, so they are constants rather than an enum:
/// no integer conversions to keep in sync, no JSON munging when the record is
/// read back from a log.
/// </summary>
public static class WalkForwardPhase
{
    public const string Is        = "IS";
    public const string Oos       = "OOS";
    public const string Aggregate = "Aggregate";
}

/// <summary>
/// Everything the user can configure before a walk-forward run.
///
/// <para><see cref="IsLength"/>, <see cref="OosLength"/>, <see cref="StepLength"/>
/// are expressed in <see cref="LengthUnit"/>: "Bars" means literal bar counts,
/// "Percent" means percent of the dataset's total bar count. The planner
/// resolves both to bar counts before emitting windows.</para>
///
/// <para><see cref="Optimization"/> is reused verbatim from the existing grid
/// optimizer - grid cap, coarse/refine iterations, mean block size, drawdown
/// rules, starting capital. No duplication.</para>
/// </summary>
public sealed record WalkForwardRunConfig(
    string WindowMode,      // "Anchored" | "Rolling"
    string LengthUnit,      // "Bars"     | "Percent"
    double IsLength,
    double OosLength,
    double StepLength,
    int    MinWindows,
    string Objective,
    int    CarryTopN,
    string Backend,         // provenance only - "Auto" | "Cpu" | "Gpu" at run start
    OptimizationRunConfig Optimization);

/// <summary>
/// One progress event. <see cref="Fraction"/> is monotonic 0..1 across the
/// whole run; the VM maps it straight onto the progress bar.
/// </summary>
public sealed record WalkForwardProgress(
    int WindowIndex,
    int TotalWindows,
    string Phase,
    double? BestIsMetric,
    double? BestOosMetric,
    double Fraction,
    double EtaSeconds);

/// <summary>
/// One non-winning OOS evaluation, kept only when <c>CarryTopN &gt; 1</c>.
/// v1 records it for provenance; nothing in the UI surfaces it yet.
/// </summary>
public sealed record WalkForwardCandidate(
    int IsRank,
    ParameterSet[] Parameters,
    double IsObjective,
    double OosObjective,
    int OosTradeCount,
    double OosNetPnl);

/// <summary>
/// One window's result. Successful and failed windows share this shape;
/// <see cref="Success"/> plus <see cref="Error"/> distinguishes them.
///
/// <para><see cref="OosEquityCurve"/> is window-local (starts at 0) and
/// downsampled to &lt;= 256 points. Empty when the MC pass dropped curves
/// (CurvesRetained == false) or the window failed.</para>
/// </summary>
public sealed record WalkForwardWindowResult(
    int Index,
    int IsStartBar, int IsEndBar,
    int OosStartBar, int OosEndBar,
    DateTime IsStartUtc, DateTime IsEndUtc,
    DateTime OosStartUtc, DateTime OosEndUtc,
    ParameterSet[] ChosenParameters,
    double IsObjective,
    double OosObjective,
    double Degradation,
    int    OosTradeCount,
    double OosNetPnl,
    double OosMaxDrawdown,
    double OosTerminalBalance,
    bool   OosBeatNull,
    McSummary? OosMc,
    double[] OosEquityCurve,
    WalkForwardCandidate[] ExtraCandidates,
    int    IsCombinationsEvaluated,
    bool   Success,
    string? Error,
    long   DurationMs);

/// <summary>
/// Aggregate statistics over the successful windows of one batch.
/// All percentages are 0..100. Means/medians exclude failed windows.
/// </summary>
public sealed record WalkForwardSummary(
    int WindowCount,
    int SuccessfulWindows,
    double MeanOosObjective,
    double MedianOosObjective,
    double MeanDegradation,
    double StitchedOosNetPnl,
    double StitchedOosMaxDrawdown,
    double PctWindowsPositiveOosPnl,
    double PctWindowsBeatNull,
    double ConsistencyScore,
    double[] StitchedEquityCurve);

/// <summary>
/// One complete walk-forward run. Written to
/// <c>data/walkforward/wf_{Id}.json</c>. <see cref="SchemaVersion"/> is bumped
/// on any breaking change; loaders reject a file whose version they do not know.
/// </summary>
public sealed record WalkForwardBatch(
    int    SchemaVersion,
    string Id,
    string StrategyName,
    string StrategyPath,
    string Symbol,
    string Timeframe,
    string DatasetLabel,
    DateTime RunUtc,
    int    TotalBars,
    double DataSpanYears,
    int    CombinationCount,
    bool   Cancelled,
    string[] ParameterNames,
    WalkForwardRunConfig Config,
    WalkForwardWindowResult[] Windows,
    WalkForwardSummary Summary)
{
    /// <summary>The schema version this build of the app writes and understands.</summary>
    public const int CurrentSchema = 1;
}