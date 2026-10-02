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
    IReadOnlyList<Mt5Tick>? NewTicks = null,
    double Percent = 0,
    double TicksPerSecond = 0,
    double EtaSeconds = 0,
    int CurrentChunk = 0,
    int TotalChunks = 0);

/// <summary>
/// Result of an EA-side CopyTicksRange ground-truth query.
/// Produced by the COUNT_TICKS action.
/// </summary>
public record Mt5CountResult(
    int Count,
    long FirstMsc,
    long LastMsc,
    long PerfUs);

/// <summary>
/// v1.16: interim progress report emitted by the EA during a chunked
/// COUNT_TICKS. Total is the EA's own estimate and may shrink mid-flight
/// if a chunk fails and the EA adapts chunk_ms downward.
/// </summary>
public record Mt5CountProgress(
    int ChunksDone,
    int ChunksTotal,
    long CumulativeTicks);