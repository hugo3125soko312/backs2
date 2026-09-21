using System;

namespace backs2.Core.Providers;

public static class TimeframeMap
{
    // Canonical timeframes exposed in the UI.
    public static readonly string[] All =
    {
        "1m", "2m", "3m", "5m", "15m", "30m", "45m",
        "1h", "2h", "3h", "4h",
        "1D", "1W"
    };

    public static bool IsIntraday(string tf)
        => tf.EndsWith('m') || tf.EndsWith('h');

    // Massive: {multiplier}/{timespan}
    public static (int Multiplier, string Timespan)? ToMassive(string tf)
    {
        return tf switch
        {
            "1m"  => (1,  "minute"),
            "2m"  => (2,  "minute"),
            "3m"  => (3,  "minute"),
            "5m"  => (5,  "minute"),
            "15m" => (15, "minute"),
            "30m" => (30, "minute"),
            "45m" => (45, "minute"),
            "1h"  => (1,  "hour"),
            "2h"  => (2,  "hour"),
            "3h"  => (3,  "hour"),
            "4h"  => (4,  "hour"),
            "1D"  => (1,  "day"),
            "1W"  => (1,  "week"),
            _     => null
        };
    }

    // Yahoo: interval string. Returns null for unsupported.
    public static string? ToYahoo(string tf)
    {
        return tf switch
        {
            "1m"  => "1m",
            "2m"  => "2m",
            "3m"  => null,   // Yahoo doesn't support
            "5m"  => "5m",
            "15m" => "15m",
            "30m" => "30m",
            "45m" => null,   // not supported
            "1h"  => "60m",
            "2h"  => null,   // not supported natively
            "3h"  => null,
            "4h"  => null,
            "1D"  => "1d",
            "1W"  => "1wk",
            _     => null
        };
    }

    // Lookback window in days, by timeframe.
    public static int LookbackDays(string tf)
    {
        return tf switch
        {
            "1m"  => 5,
            "2m"  => 10,
            "3m"  => 15,
            "5m"  => 30,
            "15m" => 60,
            "30m" => 90,
            "45m" => 90,
            "1h"  => 180,
            "2h"  => 365,
            "3h"  => 365,
            "4h"  => 365,
            "1D"  => 730,
            "1W"  => 1825,
            _     => 365
        };
    }
}