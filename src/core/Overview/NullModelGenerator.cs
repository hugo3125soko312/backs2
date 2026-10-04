using System;
using System.Collections.Generic;
using backs2.Core.Runner;

namespace backs2.Core.Overview;

/// <summary>
/// Generates a "random-signal" counterfactual trade list from an observed
/// trade list. Entry index, exit index, entry price, exit price, size, and
/// timing are preserved exactly; only the side is coin-flipped, and PnL /
/// PnLPercent reverse accordingly.
///
/// The resulting trades are passed through the normal
/// <see cref="TimeSliceBuilder.Build(IReadOnlyList{Trade}, IReadOnlyList{Models.Kline})"/>
/// path so the null model runs in the same time domain as the real strategy.
///
/// Deterministic for a fixed seed so a fixed Monte Carlo run produces the
/// same null model on every invocation.
/// </summary>
public static class NullModelGenerator
{
    public static List<Trade> GenerateRandomSignalTrades(IReadOnlyList<Trade> observed, int? seed)
    {
        var rng = seed.HasValue ? new Random(seed.Value) : new Random();
        var result = new List<Trade>(observed.Count);

        for (int i = 0; i < observed.Count; i++)
        {
            var t = observed[i];
            bool flip = rng.Next(2) == 0;

            if (!flip)
            {
                result.Add(t);
                continue;
            }

            string newSide = string.Equals(t.Side, "Long", StringComparison.OrdinalIgnoreCase)
                ? "Short"
                : "Long";

            // Prices and indices stay identical. Opposite side through the same
            // price path means PnL and PnLPercent both reverse. TimeSliceBuilder
            // does not recompute PnL from prices — it reads t.PnL on the exit
            // slice — so the reversal must be written here.
            result.Add(t with
            {
                Side = newSide,
                PnL = -t.PnL,
                PnLPercent = -t.PnLPercent
            });
        }

        return result;
    }
}