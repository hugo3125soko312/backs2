using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace backs2.Views;

/// <summary>
/// Converts Color to SolidColorBrush for the color swatch
/// shown on the Config tab's theme rows.
/// </summary>
public sealed class ColorToBrushConverter : IValueConverter
{
    public static readonly ColorToBrushConverter Instance = new();

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is Color c ? new SolidColorBrush(c) : Brushes.Transparent;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is SolidColorBrush b ? b.Color : Colors.Transparent;
}
