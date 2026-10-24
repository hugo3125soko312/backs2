using System;
using System.Collections.Concurrent;
using System.Collections.Generic;

namespace backs2.Core.Utils;

/// <summary>
/// DST-aware Eastern Time conversion for UTC timestamps.
///
/// The original implementation called TimeZoneInfo.ConvertTimeFromUtc on every
/// bar. Phase 33 profiling proved that call is the entire cost of a Roslyn
/// strategy invoke: 60,540 calls per version on a 30m GOLD.pro dataset. This
/// version replaces the per-call rule-engine walk with a per-year transition
/// table, built lazily on first access.
///
/// Correctness contract: ToEastern(long) and ToEastern(DateTime) return the
/// same DateTime as TimeZoneInfo.ConvertTimeFromUtc with the IANA zone
/// America/New_York, including the ambiguous fall-back hour and the
/// spring-forward boundary.
/// </summary>
public static class EasternTime
{
    /// <summary>IANA zone id. Cross-platform via the CLR's built-in id mapping.</summary>
    public const string TimeZoneId = "America/New_York";

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    private const long HourMs = 3_600_000L;
    private const long MinuteMs = 60_000L;

    private sealed class YearInfo
    {
        public long[] TransitionUtcMs = Array.Empty<long>();
        public long[] OffsetAfterMs = Array.Empty<long>();
        public long InitialOffsetMs;
    }

    private static readonly ConcurrentDictionary<int, YearInfo> Years = new();

    /// <summary>Converts a Unix-ms UTC timestamp to Eastern Time wall-clock.</summary>
    public static DateTime ToEastern(long unixMs)
    {
        var info = GetYear(YearOf(unixMs));

        long offsetMs = info.InitialOffsetMs;
        for (int i = 0; i < info.TransitionUtcMs.Length; i++)
        {
            if (unixMs < info.TransitionUtcMs[i]) break;
            offsetMs = info.OffsetAfterMs[i];
        }

        var utc = DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime;
        return DateTime.SpecifyKind(utc.AddMilliseconds(offsetMs), DateTimeKind.Unspecified);
    }    /// <summary>
    /// Converts a UTC DateTime to Eastern Time wall-clock. Defensive against
    /// Local and Unspecified kinds; Unspecified is treated as UTC.
    /// </summary>
    public static DateTime ToEastern(DateTime utc)
    {
        var u = utc.Kind switch
        {
            DateTimeKind.Utc => utc,
            DateTimeKind.Local => utc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            _ => utc
        };
        long ms = new DateTimeOffset(u).ToUnixTimeMilliseconds();
        return ToEastern(ms);
    }

    /// <summary>Eastern Time hour-of-day (0-23) for a Unix-ms UTC timestamp.</summary>
    public static int HourOf(long unixMs) => ToEastern(unixMs).Hour;

    /// <summary>Eastern Time minute-of-hour (0-59) for a Unix-ms UTC timestamp.</summary>
    public static int MinuteOf(long unixMs) => ToEastern(unixMs).Minute;

    /// <summary>
    /// True when the timestamp falls exactly at the given Eastern Time
    /// wall-clock hour:minute. Intended for bar-aligned checks on 1m/15m data.
    /// </summary>
    public static bool IsAt(long unixMs, int hour, int minute = 0)
    {
        var et = ToEastern(unixMs);
        return et.Hour == hour && et.Minute == minute;
    }

    /// <summary>
    /// Verifies the fast path against TimeZoneInfo.ConvertTimeFromUtc for
    /// probeCount random instants in [1990-01-01, now). Returns the mismatch
    /// count. Caller supplies the seed for reproducibility.
    /// </summary>
    public static int VerifyAgainstFramework(int probeCount, int seed)
    {
        var rng = new Random(seed);
        long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        long minMs = new DateTimeOffset(1990, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        int mismatches = 0;
        for (int i = 0; i < probeCount; i++)
        {
            long ms = rng.NextInt64(minMs, nowMs);
            var expected = TimeZoneInfo.ConvertTimeFromUtc(
                DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime, Eastern);
            var actual = ToEastern(ms);
            if (expected != actual) mismatches++;
        }
        return mismatches;
    }    private static int YearOf(long unixMs) =>
        DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime.Year;

    private static YearInfo GetYear(int year)
    {
        if (Years.TryGetValue(year, out var cached)) return cached;

        YearInfo built;
        try
        {
            built = BuildYear(year);
        }
        catch
        {
            built = new YearInfo
            {
                InitialOffsetMs = (long)Eastern.GetUtcOffset(
                    new DateTime(year, 1, 15, 12, 0, 0, DateTimeKind.Utc)).TotalMilliseconds,
            };
        }
        return Years.GetOrAdd(year, built);
    }

    private static YearInfo BuildYear(int year)
    {
        long yearStart = new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        long yearEnd = new DateTimeOffset(year + 1, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();

        long prevMs = yearStart;
        long prevOffset = OffsetMs(prevMs);
        var trans = new List<KeyValuePair<long, long>>(2);

        for (long t = yearStart + HourMs; t < yearEnd; t += HourMs)
        {
            long off = OffsetMs(t);
            if (off != prevOffset)
            {
                long boundary = BisectBoundary(prevMs, t, prevOffset);
                trans.Add(new KeyValuePair<long, long>(boundary, off));
                prevOffset = off;
            }
            prevMs = t;
        }

        var info = new YearInfo
        {
            InitialOffsetMs = OffsetMs(yearStart),
            TransitionUtcMs = new long[trans.Count],
            OffsetAfterMs = new long[trans.Count],
        };
        for (int i = 0; i < trans.Count; i++)
        {
            info.TransitionUtcMs[i] = trans[i].Key;
            info.OffsetAfterMs[i] = trans[i].Value;
        }
        return info;
    }

    private static long OffsetMs(long utcMs)
    {
        var utc = DateTimeOffset.FromUnixTimeMilliseconds(utcMs).UtcDateTime;
        return (long)Eastern.GetUtcOffset(utc).TotalMilliseconds;
    }

    private static long BisectBoundary(long lo, long hi, long loOffset)
    {
        while (hi - lo > MinuteMs)
        {
            long mid = lo + ((hi - lo) / MinuteMs / 2) * MinuteMs;
            if (OffsetMs(mid) == loOffset) lo = mid;
            else hi = mid;
        }
        return hi;
    }
}