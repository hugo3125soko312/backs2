using System;
using backs2.Core.Computing;
using backs2.Core.Overview;

namespace backs2.Core.Computing.Backends;

/// <summary>
/// The Monte Carlo state machine on the CPU — the authoritative implementation
/// of the per-iteration body, consumed by <see cref="CpuBackend"/> through
/// <see cref="ISimulationKernel"/>.
///
/// <para><b>Lineage.</b> This is a verbatim port of the private
/// <c>MonteCarloEngine.SimulateOnce</c> that shipped through Phase 29A, moved
/// here so the engine can reach its own state machine through
/// <c>IComputeDispatcher</c> without the aggregation half of the engine knowing
/// how a kernel runs. The statement order is the specification and must not be
/// reshuffled: MTM-low vs daily floor check, then max-DD floor check, then
/// realize PnL, then HWM update, then EOD reset. The GPU kernel
/// (<see cref="SimulationKernelGpu"/>) reproduces this same order.</para>
///
/// <para><b>RNG.</b> Per-iteration RNGs are split exactly as the engine always
/// split them — <c>new Random(seed + i)</c> keyed on the absolute iteration
/// index, or <see cref="Random.Shared"/> when the caller passed no seed.</para>
/// </summary>
public sealed class SimulationKernelCpu : ISimulationKernel
{
    /// <summary>Shared instance; the kernel is stateless and thread-safe.</summary>
    public static readonly SimulationKernelCpu Instance = new();

    /// <inheritdoc/>
    public MonteCarloIterationOutcome Simulate(
        TimeSlice[] slices,
        MonteCarloSimulationRequest request,
        Random rng,
        int iteration,
        double[]? curve)
    {
        int n = slices.Length;
        int targetDurationSlices = request.TargetDurationSlices;

        double startingCapital = request.StartingCapital;
        double balance = startingCapital;
        double hwm = startingCapital;
        double dayStartBalance = startingCapital;
        double peak = startingCapital;
        double maxDd = 0;

        double dayRealized = 0;
        double maxDayRealized = 0;

        int losingStreak = 0;
        int longestLosing = 0;

        int flatStretch = 0;
        int longestFlat = 0;

        byte cause = MonteCarloRuinCause.None;

        int elapsed = 0;
        byte result = MonteCarloOutcome.Timeout;

        int produced = 0;
        double p = 1.0 / request.ExpectedBlockSize;

        bool ended = false;

        while (produced < targetDurationSlices && !ended)
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
                    if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;
                    dayRealized = 0;

                    dayStartBalance = balance;

                    if (request.TrailingEod && balance > hwm) hwm = balance;
                }

                double mtmBalance = balance + s.MtmLow;

                double dailyFloor = dayStartBalance - request.DailyFloorLoss;

                double maxFloor;
                if (request.TrailingIntraday)
                    maxFloor = hwm - request.MaxFloorLoss;
                else if (request.TrailingEod)
                    maxFloor = hwm - request.MaxFloorLoss;
                else
                    maxFloor = request.StaticMaxFloor;

                bool dailyHit = mtmBalance <= dailyFloor;
                bool maxHit = mtmBalance <= maxFloor;

                if (dailyHit || maxHit)
                {
                    double worst = Math.Min(dailyFloor, maxFloor);
                    balance = worst;
                    if (curve != null) curve[produced] = balance;
                    result = MonteCarloOutcome.Blown;
                    elapsed = produced + 1;

                    if (dailyHit && maxHit)
                        cause = dailyFloor >= maxFloor ? MonteCarloRuinCause.DailyFloor : MonteCarloRuinCause.MaxFloor;
                    else if (dailyHit)
                        cause = MonteCarloRuinCause.DailyFloor;
                    else
                        cause = MonteCarloRuinCause.MaxFloor;

                    ended = true;
                    break;
                }

                double peakBefore = peak;
                double mtmHigh = balance + s.MtmHigh;
                if (mtmHigh > peak) peak = mtmHigh;

                balance += s.RealizedPnl;
                dayRealized += s.RealizedPnl;

                if (s.RealizedPnl < 0)
                {
                    losingStreak++;
                    if (losingStreak > longestLosing) longestLosing = losingStreak;
                }
                else if (s.RealizedPnl > 0)
                {
                    losingStreak = 0;
                }

                if (peak > peakBefore)
                    flatStretch = 0;
                else
                    flatStretch++;
                if (flatStretch > longestFlat) longestFlat = flatStretch;

                if (request.TrailingIntraday && balance > hwm) hwm = balance;

                double dd = peak > 0 ? (peak - balance) / peak : 0;
                if (dd > maxDd) maxDd = dd;

                if (curve != null) curve[produced] = balance;

                if (request.ProfitTarget > 0 && balance - startingCapital >= request.ProfitTarget)
                {
                    result = MonteCarloOutcome.Funded;
                    elapsed = produced + 1;
                    ended = true;
                    break;
                }
            }
        }

        if (!ended)
            elapsed = targetDurationSlices;

        if (dayRealized > maxDayRealized) maxDayRealized = dayRealized;

        double terminalBalance = balance;
        double profit = terminalBalance - startingCapital;

        return new MonteCarloIterationOutcome(
            TerminalBalance: terminalBalance,
            Outcome: result,
            SlicesElapsed: elapsed,
            MaxDrawdownPercent: maxDd * 100.0,
            BestDayRatio: (profit > 0 && maxDayRealized > 0) ? maxDayRealized / profit : 0.0,
            LongestLosingStreak: longestLosing,
            LongestFlatStretch: longestFlat,
            RuinCause: cause);
    }
}
