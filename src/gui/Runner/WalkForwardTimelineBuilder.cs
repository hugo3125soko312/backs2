using System;
using System.Collections.Generic;
using System.Globalization;
using backs2.Core.WalkForward;

namespace backs2.ViewModels;

/// <summary>
/// Pure function that converts WalkForwardWindowRow collections into plot-ready shapes.
/// Testable without UI dependencies.
/// </summary>
public static class WalkForwardTimelineBuilder
{
    public record TimelineBar(
        int WindowIndex,
        DateTime IsStart,
        DateTime IsEnd,
        DateTime OosStart,
        DateTime OosEnd,
        double OosNetPnl,
        double Degradation,
        bool Success,
        int OosTradeCount,
        double OosMaxDrawdown);

    public record SummaryMetrics(
        int WindowCount,
        double TotalOosPnl,
        double WorstDrawdown,
        double AvgDegradation);

    /// <summary>
    /// Convert window rows to timeline bars with date conversion.
    /// </summary>
    /// <param name="rows">Source window rows</param>
    /// <param name="barTimestamps">Timestamp lookup for bar indices</param>
    /// <returns>Timeline bars with real dates</returns>
    public static IReadOnlyList<TimelineBar> Build(
        IReadOnlyList<WalkForwardWindowRow> rows,
        IReadOnlyList<DateTime> barTimestamps)
    {
        var result = new List<TimelineBar>(rows.Count);
        
        for (int i = 0; i < rows.Count; i++)
        {
            var r = rows[i];
            
            DateTime isStart = BarToDateTime(r.IsStartBar, barTimestamps);
            DateTime isEnd = BarToDateTime(r.IsEndBar, barTimestamps);
            DateTime oosStart = BarToDateTime(r.OosStartBar, barTimestamps);
            DateTime oosEnd = BarToDateTime(r.OosEndBar, barTimestamps);
            
            result.Add(new TimelineBar(
                WindowIndex: r.WindowIndex,
                IsStart: isStart,
                IsEnd: isEnd,
                OosStart: oosStart,
                OosEnd: oosEnd,
                OosNetPnl: r.OosNetPnl,
                Degradation: r.Degradation,
                Success: r.Success,
                OosTradeCount: r.OosTradeCount,
                OosMaxDrawdown: r.OosMaxDrawdown));
        }
        
        return result;
    }

    /// <summary>
    /// Convert bar index to DateTime using lookup table.
    /// </summary>
    private static DateTime BarToDateTime(int barIndex, IReadOnlyList<DateTime> timestamps)
    {
        if (barIndex < 0 || barIndex >= timestamps.Count)
            return DateTime.MinValue;
        
        return timestamps[barIndex];
    }

    /// <summary>
    /// Calculate summary metrics from bars.
    /// </summary>
    public static SummaryMetrics CalculateSummary(IReadOnlyList<TimelineBar> bars)
    {
        if (bars.Count == 0)
            return new SummaryMetrics(0, 0, 0, 0);
        
        double totalPnl = 0;
        double worstDd = 0;
        double totalDegradation = 0;
        
        foreach (var bar in bars)
        {
            totalPnl += bar.OosNetPnl;
            worstDd = Math.Max(worstDd, bar.OosMaxDrawdown);
            totalDegradation += bar.Degradation;
        }
        
        return new SummaryMetrics(
            WindowCount: bars.Count,
            TotalOosPnl: totalPnl,
            WorstDrawdown: worstDd,
            AvgDegradation: totalDegradation / bars.Count);
    }

    /// <summary>
    /// Determine health color for a bar based on degradation and PnL.
    /// </summary>
    public static string GetHealthColor(double degradation, double oosNetPnl)
    {
        if (oosNetPnl < 0 || degradation > 0.50)
            return "Red";
        if (degradation >= 0.25)
            return "Amber";
        return "Green";
    }

    /// <summary>
    /// Format PnL display string.
    /// </summary>
    public static string FormatPnl(double value)
    {
        return value.ToString("F2", CultureInfo.InvariantCulture);
    }
}
