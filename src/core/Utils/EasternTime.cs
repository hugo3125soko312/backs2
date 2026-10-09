using System;

namespace backs2.Core.Utils;

/// <summary>
/// DST-aware Eastern Time conversion for UTC timestamps.
///
/// Uses the IANA zone id "America/New_York", which .NET 8 resolves on every
/// supported platform (Windows included — the CLR performs the IANA ↔ Windows
/// mapping internally). The zone handles US DST transitions natively: 09:00 ET
/// is 14:00 UTC in winter (EST, UTC-5) and 13:00 UTC in summer (EDT, UTC-4).
/// No branching on the calendar is needed at the call site.
///
/// Why this exists: strategy scripts in data/strategies/*.cs operate on
/// List&lt;Kline&gt; whose Timestamp fields are UTC DateTime values. Any strategy
/// that targets a specific ET wall-clock hour needs this conversion, and
/// hand-rolling it against a hardcoded UTC hour breaks across the DST
/// transition.
/// </summary>
public static class EasternTime
{
    /// <summary>IANA zone id. Cross-platform via the CLR's built-in id mapping.</summary>
    public const string TimeZoneId = "America/New_York";

    private static readonly TimeZoneInfo Eastern =
        TimeZoneInfo.FindSystemTimeZoneById(TimeZoneId);

    /// <summary>Converts a Unix-ms UTC timestamp to Eastern Time wall-clock.</summary>
    public static DateTime ToEastern(long unixMs)
        => TimeZoneInfo.ConvertTimeFromUtc(
            DateTimeOffset.FromUnixTimeMilliseconds(unixMs).UtcDateTime,
            Eastern);

    /// <summary>
    /// Converts a UTC DateTime to Eastern Time wall-clock. Defensive against
    /// Local and Unspecified kinds; Unspecified is treated as UTC.
    /// </summary>
    public static DateTime ToEastern(DateTime utc)
    {
        var u = utc.Kind switch
        {
            DateTimeKind.Utc         => utc,
            DateTimeKind.Local       => utc.ToUniversalTime(),
            DateTimeKind.Unspecified => DateTime.SpecifyKind(utc, DateTimeKind.Utc),
            _                        => utc
        };
        return TimeZoneInfo.ConvertTimeFromUtc(u, Eastern);
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
}