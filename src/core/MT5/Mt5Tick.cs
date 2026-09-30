using System;
using System.Collections.Generic;

namespace backs2.Core.MT5;

public readonly record struct Mt5Tick(
    long TimeMsc,
    double Bid,
    double Ask,
    double Last,
    ulong Volume,
    double VolumeReal,
    uint Flags)
{
    public DateTime Timestamp
        => DateTimeOffset.FromUnixTimeMilliseconds(TimeMsc).UtcDateTime;

    public string FlagsDisplay
    {
        get
        {
            var parts = new List<string>(4);
            if ((Flags & 2)  != 0) parts.Add("Bid");
            if ((Flags & 4)  != 0) parts.Add("Ask");
            if ((Flags & 8)  != 0) parts.Add("Last");
            if ((Flags & 16) != 0) parts.Add("Vol");
            if ((Flags & 32) != 0) parts.Add("Buy");
            if ((Flags & 64) != 0) parts.Add("Sell");
            return parts.Count > 0 ? string.Join(", ", parts) : "—";
        }
    }
}

public record Mt5TerminalInfo(
    string Company,
    long Account,
    string Server,
    string Currency,
    long Leverage,
    DateTime ServerTimeUtc);

public record Mt5PingResult(bool Success, int LatencyMs, string Error = "");

public record Mt5FetchProgress(
    int Fetched,
    int Total,
    string Phase,
    IReadOnlyList<Mt5Tick>? NewTicks = null);