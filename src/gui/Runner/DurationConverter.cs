using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace backs2.Views;

/// <summary>
/// Converts milliseconds to a human-readable duration string (e.g. "1d 2h 30m").
/// </summary>
public sealed class DurationConverter : IValueConverter
{
    public static readonly DurationConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not long ms)
            return value;

        if (ms < 0)
            return $"-{FormatDuration(-ms)}";

        return FormatDuration(ms);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }

    private static string FormatDuration(long totalMs)
    {
        var ts = TimeSpan.FromMilliseconds(totalMs);

        if (ts.TotalDays >= 1)
            return $"{ts.Days}d {ts.Hours}h";

        if (ts.TotalHours >= 1)
            return $"{ts.Hours}h {ts.Minutes}m";

        return $"{ts.Minutes}m {ts.Seconds}s";
    }
}
