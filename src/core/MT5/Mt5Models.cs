using System.Collections.Generic;

namespace backs2.Core.MT5;

public sealed record Mt5PingResult(bool Success, int LatencyMs, string Error = "");

public sealed record Mt5FetchProgress(
    int Fetched,
    int Total,
    string Phase,
    IReadOnlyList<Mt5Tick>? NewTicks = null,
    double Percent = 0,
    double TicksPerSecond = 0,
    double EtaSeconds = 0,
    int CurrentChunk = 0,
    int TotalChunks = 0);

public sealed record Mt5SyncResult(int SyncedDays, int SkippedDays, int TotalDays);

/// <summary>
/// Progress tick for either COUNT_TICKS (chunked mode) or
/// SYNC_TICKS_RANGE. Both actions emit "action":"PROGRESS" frames with
/// chunks_done / chunks_total; the payload differs:
///   COUNT_TICKS progress  → cumulative_ticks
///   SYNC progress         → synced_days, skipped_days
/// Unused fields default to 0.
/// </summary>
public sealed record Mt5CountProgress(
    int RequestId,
    int ChunksDone,
    int ChunksTotal,
    long CumulativeTicks = 0,
    int SyncedDays = 0,
    int SkippedDays = 0);

public sealed record Mt5CountResult(int Count, long FirstMsc, long LastMsc, long PerfUs);