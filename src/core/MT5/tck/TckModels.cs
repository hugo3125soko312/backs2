// src/core/MT5/Tkc/TkcModels.cs
using System;

namespace backs2.Core.MT5.Tkc;

/// <summary>
/// A single price tick decoded from a MetaTrader 5 .tkc tick-cache file.
/// The .tkc format stores only timestamp, bid, and ask — no volume, no
/// last, no flags. Volume must be reconstructed downstream (via tick count
/// per bar, which is what the Parsing subtab already does).
/// </summary>
public readonly record struct TkcTick(long TimestampMs, double Bid, double Ask)
{
    public DateTime TimestampUtc
        => DateTimeOffset.FromUnixTimeMilliseconds(TimestampMs).UtcDateTime;

    public double Spread => Ask - Bid;
}

/// <summary>
/// Metadata for a .tkc file. Produced after a full decode pass; the format
/// carries no random-access index, so TickCount requires decoding the file.
/// </summary>
public sealed record TkcInfo(
    uint Magic,
    string Symbol,
    long FileSize,
    int TickCount,
    long? StartMs,
    long? EndMs)
{
    public DateTime? StartUtc => StartMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(StartMs.Value).UtcDateTime
        : null;

    public DateTime? EndUtc => EndMs.HasValue
        ? DateTimeOffset.FromUnixTimeMilliseconds(EndMs.Value).UtcDateTime
        : null;
}

/// <summary>
/// Raised for any .tkc parsing failure: bad magic, unreadable symbol,
/// truncated header, empty tick set, or zlib stream that will not inflate.
/// </summary>
public sealed class TkcException : Exception
{
    public TkcException(string message) : base(message) { }
    public TkcException(string message, Exception inner) : base(message, inner) { }
}