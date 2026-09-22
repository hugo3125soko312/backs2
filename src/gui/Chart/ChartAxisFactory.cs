using System.Globalization;
using OxyPlot;
using OxyPlot.Axes;

namespace backs2.ViewModels;

internal static class ChartAxisFactory
{
    private static readonly OxyColor Background = OxyColor.Parse("#1E1E1E");
    private static readonly OxyColor BorderColor = OxyColor.Parse("#333333");
    private static readonly OxyColor TickColor = OxyColor.Parse("#555555");
    private static readonly OxyColor MutedText = OxyColor.Parse("#888888");

    public static PlotModel CreateEmptyModel() => new()
    {
        Background = Background,
        PlotAreaBackground = Background,
        TextColor = OxyColors.White,
        PlotAreaBorderColor = BorderColor,
        Padding = new OxyThickness(0)
    };

    public static DateTimeAxis CreateDateAxis() => new()
    {
        Position = AxisPosition.Bottom,
        StringFormat = "yyyy",
        IntervalType = DateTimeIntervalType.Years,
        MinorIntervalType = DateTimeIntervalType.Months,
        TicklineColor = TickColor,
        MajorGridlineStyle = LineStyle.None,
        MinorGridlineStyle = LineStyle.None,
        TextColor = OxyColors.White
    };

    public static Axis CreatePriceAxis(ChartScaleState scale, double start, double end)
    {
        double s = scale.Inverted ? end : start;
        double e = scale.Inverted ? start : end;

        if (scale.Logarithmic)
        {
            return new LogarithmicAxis
            {
                Key = "Price",
                Position = AxisPosition.Right,
                StartPosition = s,
                EndPosition = e,
                TicklineColor = TickColor,
                MajorGridlineStyle = LineStyle.None,
                MinorGridlineStyle = LineStyle.None,
                TextColor = OxyColors.White
            };
        }

        return new LinearAxis
        {
            Key = "Price",
            Position = AxisPosition.Right,
            StartPosition = s,
            EndPosition = e,
            TicklineColor = TickColor,
            MajorGridlineStyle = LineStyle.None,
            MinorGridlineStyle = LineStyle.None,
            TextColor = OxyColors.White
        };
    }

    public static LinearAxis CreateVolumeAxis(double start, double end) => new()
    {
        Key = "Volume",
        Position = AxisPosition.Right,
        StartPosition = start,
        EndPosition = end,
        Minimum = 0,
        TicklineColor = TickColor,
        MajorGridlineStyle = LineStyle.None,
        MinorGridlineStyle = LineStyle.None,
        TextColor = MutedText,
        FontSize = 10,
        LabelFormatter = FormatVolume
    };

    private static string FormatVolume(double value)
    {
        if (value <= 0) return "0";

        double abs = System.Math.Abs(value);
        if (abs >= 1_000_000_000_000d) return (value / 1_000_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "T";
        if (abs >= 1_000_000_000d)     return (value / 1_000_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "B";
        if (abs >= 1_000_000d)         return (value / 1_000_000d).ToString("0.#", CultureInfo.InvariantCulture) + "M";
        if (abs >= 1_000d)             return (value / 1_000d).ToString("0.#", CultureInfo.InvariantCulture) + "K";
        return value.ToString("0", CultureInfo.InvariantCulture);
    }
}