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
    public static TimeSlice[] Build(IReadOnlyList<Trade> trades, IReadOnlyList<Kline> klines)
    {
        if (klines == null || klines.Count == 0)
            return Array.Empty<TimeSlice>();

        int n = klines.Count;
        var slices = new TimeSlice[n];

        // Sort trades by entry index so overlap detection is a single forward pass.
        var sorted = new List<Trade>(trades);
        sorted.Sort((a, b) => a.EntryIndex.CompareTo(b.EntryIndex));

        // Pre-fill every slice with no MTM, no PnL, and the EOD flag computed
        // from the day boundary.
        byte prevDay = 255;
        for (int i = 0; i < n; i++)
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
}