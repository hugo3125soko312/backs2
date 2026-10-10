using System;
using System.Collections.Generic;

namespace backs2.Core.Computing;

/// <summary>
/// Per-iteration parameters of the Monte Carlo state machine — everything
/// <c>MonteCarloEngine.SimulateOnce</c> needs that is constant across the whole
/// batch. Grouped into one value so the CPU kernel and the GPU
/// <c>[Shader]</c> kernel can take the identical shape; the GPU side consumes it
/// as a uniform constant buffer, so field order and types are part of the ABI.
///
/// <para><b>Floors are pre-scaled.</b> <see cref="DailyFloorLoss"/>,
/// <see cref="MaxFloorLoss"/> and <see cref="ProfitTarget"/> are the absolute
/// money amounts the engine already derives from the percentage settings —
/// the kernel never sees a percentage.</para>
/// </summary>
/// <param name="TargetDurationSlices">Slices each simulated run must produce.</param>
/// <param name="StartingCapital">Balance every run starts from.</param>
/// <param name="DailyFloorLoss">Absolute daily loss floor (starting capital × daily percent).</param>
/// <param name="MaxFloorLoss">Absolute max loss floor (starting capital × max percent).</param>
/// <param name="ProfitTarget">Absolute profit target; 0 or less disables the funded outcome.</param>
/// <param name="ExpectedBlockSize">Mean bootstrap block size; the block-length geometric probability is 1/this.</param>
/// <param name="TrailingEod">True for the trailing-EOD drawdown type (HWM advances at EOD).</param>
/// <param name="TrailingIntraday">True for the trailing-intraday drawdown type (HWM advances every slice).</param>
public readonly record struct MonteCarloSimulationRequest(
    int TargetDurationSlices,
    double StartingCapital,
    int DailyFloorLoss,
    int MaxFloorLoss,
    int ProfitTarget,
    int ExpectedBlockSize,
    bool TrailingEod,
    bool TrailingIntraday)
{
    /// <summary>
    /// Absolute floor for a static drawdown type, as a double. Mirrors the
    /// engine's <c>startingCapital - maxFloorLoss</c> branch, which is taken
    /// when neither trailing flag is set.
    /// </summary>
    public double StaticMaxFloor => StartingCapital - MaxFloorLoss;
}

/// <summary>
/// One simulation's complete output — every per-iteration value
/// <c>MonteCarloEngine.SimulateOnce</c> produces, except the equity curve, which
/// is optional and written by the caller.
/// </summary>
/// <param name="TerminalBalance">Balance when the run ended.</param>
/// <param name="Outcome">One of <c>MonteCarloOutcome</c>.</param>
/// <param name="SlicesElapsed">Slices produced before the run ended.</param>
/// <param name="MaxDrawdownPercent">Peak-to-balance drawdown, already ×100 (the engine's unit).</param>
/// <param name="BestDayRatio">Best single day's realized PnL over total profit; 0 when not positive.</param>
/// <param name="LongestLosingStreak">Longest run of negative realized-PnL slices.</param>
/// <param name="LongestFlatStretch">Longest run of slices with no new mark-to-market peak.</param>
/// <param name="RuinCause">One of <c>MonteCarloRuinCause</c>; None when the run did not blow.</param>
public readonly record struct MonteCarloIterationOutcome(
    double TerminalBalance,
    byte Outcome,
    int SlicesElapsed,
    double MaxDrawdownPercent,
    double BestDayRatio,
    int LongestLosingStreak,
    int LongestFlatStretch,
    byte RuinCause);

/// <summary>
/// The per-simulation arrays a Monte Carlo dispatch produces, indexed by
/// iteration. This is the shape both backends return at every level — one chunk,
/// one whole-batch call, one dispatcher call — so nothing is reshaped or lost as
/// results move from a kernel to the engine.
///
/// <para><b>Why not <c>double[]</c>.</b> <c>MonteCarloEngine.BuildResult</c>
/// aggregates nine parallel per-iteration arrays, not just terminal balances, and
/// the retained-curve path needs the equity curves themselves. A terminal-balance
/// channel would have to be widened by the first caller that needs any of the
/// other eight, so the transfer shape is the full result from the start.</para>
/// </summary>
public sealed record MonteCarloDispatchResult(
    double[] TerminalBalances,
    byte[] Outcomes,
    int[] SlicesElapsed,
    double[] MaxDrawdowns,
    double[] BestDayRatios,
    int[] LongestLosingStreaks,
    int[] LongestFlatStretches,
    byte[] RuinCauses,
    double[][] EquityCurves)
{
    /// <summary>
    /// Number of iterations this result actually carries. A cancelled or
    /// partially completed dispatch carries fewer than the request asked for —
    /// results are truncated at a chunk boundary, never padded.
    /// </summary>
    public int Count => TerminalBalances?.Length ?? 0;

    /// <summary>True when equity curves were produced for every iteration.</summary>
    public bool CurvesRetained => EquityCurves != null && EquityCurves.Length == Count;

    /// <summary>An empty result — the fail-soft answer at every boundary.</summary>
    public static MonteCarloDispatchResult Empty { get; } = new(
        Array.Empty<double>(),
        Array.Empty<byte>(),
        Array.Empty<int>(),
        Array.Empty<double>(),
        Array.Empty<double>(),
        Array.Empty<int>(),
        Array.Empty<int>(),
        Array.Empty<byte>(),
        Array.Empty<double[]>());

    /// <summary>
    /// Stitches per-chunk results (in ascending chunk order) into one batch
    /// result. Curves are carried only when every chunk produced them.
    /// </summary>
    public static MonteCarloDispatchResult Concat(
        IReadOnlyList<MonteCarloDispatchResult> chunks)
    {
        int total = 0;
        bool curves = chunks.Count > 0;
        for (int c = 0; c < chunks.Count; c++)
        {
            total += chunks[c].TerminalBalances.Length;
            curves &= chunks[c].CurvesRetained;
        }

        var terminal = new double[total];
        var outcomes = new byte[total];
        var elapsed = new int[total];
        var drawdowns = new double[total];
        var ratios = new double[total];
        var losing = new int[total];
        var flat = new int[total];
        var causes = new byte[total];
        var equity = curves ? new double[total][] : Array.Empty<double[]>();

        int offset = 0;
        for (int c = 0; c < chunks.Count; c++)
        {
            var chunk = chunks[c];
            int length = chunk.TerminalBalances.Length;

            Array.Copy(chunk.TerminalBalances, 0, terminal, offset, length);
            Array.Copy(chunk.Outcomes, 0, outcomes, offset, length);
            Array.Copy(chunk.SlicesElapsed, 0, elapsed, offset, length);
            Array.Copy(chunk.MaxDrawdowns, 0, drawdowns, offset, length);
            Array.Copy(chunk.BestDayRatios, 0, ratios, offset, length);
            Array.Copy(chunk.LongestLosingStreaks, 0, losing, offset, length);
            Array.Copy(chunk.LongestFlatStretches, 0, flat, offset, length);
            Array.Copy(chunk.RuinCauses, 0, causes, offset, length);

            if (curves)
            {
                for (int i = 0; i < length; i++)
                    equity[offset + i] = chunk.EquityCurves[i];
            }

            offset += length;
        }

        return new MonteCarloDispatchResult(
            terminal, outcomes, elapsed, drawdowns, ratios, losing, flat, causes, equity);
    }
}
