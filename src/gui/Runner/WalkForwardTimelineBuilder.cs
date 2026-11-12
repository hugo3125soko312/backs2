using System;
using System.Collections.Generic;
using System.Linq;
using backs2.ViewModels;

namespace backs2.ViewModels;

/// <summary>
/// Converts walk-forward window rows into timeline bar data.
/// Pure function - no UI, thread-safe.
/// </summary>
public static class WalkForwardTimelineBuilder
{
    public static List<WalkForwardBar> BuildBars(IReadOnlyList<WalkForwardWindowRow> rows)
    {
        if (rows.Count == 0) return new();

        int maxWindows = rows.Max(r => r.WindowIndex) + 1;

        var bars = new List<WalkForwardBar>();
        foreach (var row in rows)
        {
            // IS bar (in-sample) - lighter color
            bars.Add(new WalkForwardBar
            {
                WindowIndex = row.WindowIndex,
                Y = row.WindowIndex,
                Height = 0.35,
                Label = "IS",
                FillColor = "#C0A068",
                BarWidth = 80
            });

            // OOS bar (out-of-sample) - colored by performance
            bars.Add(new WalkForwardBar
            {
                WindowIndex = row.WindowIndex,
                Y = row.WindowIndex + 0.4,
                Height = 0.4,
                Label = $"OOS {row.OosNetPnl:+0;-0}",
                FillColor = GetOosColor(row),
                BarWidth = 80
            });
        }

        return bars;
    }

    private static string GetOosColor(WalkForwardWindowRow row)
    {
        if (!row.Success || row.Degradation > 0.50 || row.OosNetPnl < 0) return "#E74C3C";      // red
        if (row.Degradation >= 0.25) return "#F5A623";                                            // amber
        return "#2ECC71";                                                                          // green
    }
}

public class WalkForwardBar
{
    public int WindowIndex { get; set; }
    public double Y { get; set; }
    public double Height { get; set; } = 0.4;
    public string Label { get; set; } = "";
    public string FillColor { get; set; } = "#FFFFFF";
    public int BarWidth { get; set; } = 80;
}
