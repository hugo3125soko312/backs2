using System;
using System.Collections.Generic;
using backs2.Core.Optimizer;

namespace backs2.Core.WalkForward;

/// <summary>
/// Turns a version's MC/backtest scalars into a single number, always with the
/// convention "higher is better" so selection is a simple
/// <c>OrderByDescending</c>.
/// </summary>
public static class WalkForwardObjective
{
    public const string EdgePercent           = "EdgePercent";
    public const string PFunded               = "PFunded";
    public const string NetPnl                = "NetPnl";
    public const string PRuin                 = "PRuin";
    public const string TerminalBalanceMedian = "TerminalBalanceMedian";

    /// <summary>The keys the UI offers, in display order. Single source of truth.</summary>
    public static readonly IReadOnlyList<string> SupportedKeys = new[]
    {
        EdgePercent,
        PFunded,
        NetPnl,
        PRuin,
        TerminalBalanceMedian,
    };

    /// <summary>Normalizes an arbitrary string to a key in SupportedKeys.</summary>
    public static string Normalize(string? key)
    {
        if (string.IsNullOrWhiteSpace(key)) return EdgePercent;
        foreach (var k in SupportedKeys)
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase))
                return k;
        return EdgePercent;
    }

    /// <summary>Returns the objective value for one version. Higher is better.</summary>
    public static double Select(string key, OptimizationVersion v)
    {
        if (v == null) return double.NegativeInfinity;
        switch (key)
        {
            case EdgePercent:
                return v.Mc?.EdgePercent ?? double.NegativeInfinity;
            case PFunded:
                return v.Mc?.PFunded ?? double.NegativeInfinity;
            case NetPnl:
                return v.NetPnl;
            case PRuin:
                return v.Mc is { } m ? -m.PRuin : double.NegativeInfinity;
            case TerminalBalanceMedian:
                return v.Mc?.TerminalBalanceMedian ?? double.NegativeInfinity;
            default:
                return v.Mc?.EdgePercent ?? double.NegativeInfinity;
        }
    }
}
