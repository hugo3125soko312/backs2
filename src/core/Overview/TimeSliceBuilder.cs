using System;
using System.Collections.Generic;
using backs2.Core.Models;
using backs2.Core.Runner;

namespace backs2.Core.Overview;

public static class TimeSliceBuilder
{
    /// <summary>
    /// Transforms a backtest result into a contiguous time-slice array suitable for
    /// block-bootstrap Monte Carlo evaluation.
    ///
    /// Each slice corresponds to one Kline. Slices inside an open trade carry the
    /// intra-candle mark-to-market excursion of the position. Slices between trades
    /// have zero MTM. The slice where a trade closes carries the realized PnL.
    ///
    /// The trade list must use Kline indexes (Trade.EntryIndex / Trade.ExitIndex).
    /// </summary>
    /// <summary>
    /// Optional baseline overload. <paramref name="baseline"/> is the prefilled
    /// slice array for these klines with no trades applied — identical for every
    /// optimizer version of one dataset. Passing it skips the per-version prefill
    /// pass, which is the dominant cost on a large grid (4,032 versions x 60,540
    /// bars measured at 1,094,643 ms thread-summed on 2026-10-25).
    /// </summary>
    public static TimeSlice[] Build(
        IReadOnlyList<Trade> trades,
        IReadOnlyList<Kline> klines,
        TimeSlice[]? baseline = null)
    {
        if (klines == null || klines.Count == 0)
            return Array.Empty<TimeSlice>();

        int n = klines.Count;
        var slices = new TimeSlice[n];

        // Sort trades by entry index so overlap detection is a single forward pass.
        var sorted = new List<Trade>(trades);
        sorted.Sort((a, b) => a.EntryIndex.CompareTo(b.EntryIndex));

        // Pre-fill with the EOD/timestamp baseline. A caller running many
        // versions over one dataset passes a shared baseline, so this becomes a
        // single struct copy instead of a full pass with a DateTimeOffset per
        // bar.
        if (baseline != null && baseline.Length == n)
            Array.Copy(baseline, slices, n);
        else
            FillBaseline(klines, slices);

        // Walk each trade, write MTM into every slice it spans.
        foreach (var t in sorted)
        {
            if (t.EntryIndex < 0 || t.EntryIndex >= n) continue;
            if (t.ExitIndex < 0 || t.ExitIndex >= n) continue;
            if (t.EntryIndex > t.ExitIndex) continue;

            bool isLong = string.Equals(t.Side, "Long", StringComparison.OrdinalIgnoreCase);
            double entryPrice = t.EntryPrice;
            double size = t.Size > 0 ? t.Size : 1.0;

            for (int i = t.EntryIndex; i <= t.ExitIndex; i++)
            {
                var k = klines[i];

                double mtmHigh, mtmLow;
                if (isLong)
                {
                    mtmHigh = (k.High - entryPrice) * size;
                    mtmLow = (k.Low - entryPrice) * size;
                }
                else
                {
                    mtmHigh = (entryPrice - k.Low) * size;
                    mtmLow = (entryPrice - k.High) * size;
                }

                var prev = slices[i];

                double realized = (i == t.ExitIndex) ? t.PnL : 0.0;

                slices[i] = new TimeSlice(
                    prev.Timestamp,
                    prev.MtmHigh + mtmHigh,
                    prev.MtmLow + mtmLow,
                    prev.RealizedPnl + realized,
                    prev.IsEodReset);
            }
        }

        return slices;
    }

    /// <summary>
    /// Builds the trade-independent baseline for one dataset: timestamps and EOD
    /// flags filled, all MTM and PnL zero. Produced once per optimizer run and
    /// passed to every version's <see cref="Build"/> call.
    /// </summary>
    public static TimeSlice[] CreateBaseline(IReadOnlyList<Kline> klines)
    {
        if (klines == null || klines.Count == 0)
            return Array.Empty<TimeSlice>();

        var slices = new TimeSlice[klines.Count];
        FillBaseline(klines, slices);
        return slices;
    }

    private static void FillBaseline(IReadOnlyList<Kline> klines, TimeSlice[] slices)
    {
        byte prevDay = 255;
        for (int i = 0; i < klines.Count; i++)
        {
            var k = klines[i];
            byte day = (byte)(k.Timestamp.DayOfYear % 256);
            byte eod = (i == 0) ? (byte)1 : (prevDay != day ? (byte)1 : (byte)0);
            prevDay = day;

            slices[i] = new TimeSlice(
                new DateTimeOffset(k.Timestamp).ToUnixTimeMilliseconds(),
                0,
                0,
                0,
                eod);
        }
    }
}