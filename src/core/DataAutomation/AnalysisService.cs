using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace backs2.Core.DataAutomation;

public sealed record Bar(long T, double O, double H, double L, double C, double V);

public sealed class AnalysisService
{
    public async Task<string> AnalyzeAsync(
        string path,
        int timezoneOffset,
        HashSet<int> sessionClosedHours,
        bool skipWeekend,
        CancellationToken ct = default)
    {
        if (!File.Exists(path))
            return $"File not found: {path}";

        var json = await File.ReadAllTextAsync(path, ct);
        var bars = JsonSerializer.Deserialize<List<Bar>>(json);
        if (bars == null || bars.Count == 0)
            return "No bars found in file.";

        bars.Sort((a, b) => a.T.CompareTo(b.T));

        // Determine expected timeframe (in hours) as the most common gap
        var gaps = new List<double>();
        for (int i = 1; i < bars.Count; i++)
        {
            var diff = (bars[i].T - bars[i - 1].T) / 3600000.0; // hours
            if (diff > 0) gaps.Add(diff);
        }
        double expectedHours = 1.0;
        if (gaps.Count > 0)
        {
            var groups = gaps.GroupBy(g => Math.Round(g, 2))
                             .OrderByDescending(g => g.Count());
            expectedHours = groups.First().Key;
        }

        var problems = new List<Problem>();
        int expectedClosures = 0;
        var closureKinds = new Dictionary<string, int> { ["Session"] = 0, ["Weekend"] = 0, ["Holiday?"] = 0 };

        for (int i = 1; i < bars.Count; i++)
        {
            var prev = bars[i - 1];
            var curr = bars[i];
            double gapHours = (curr.T - prev.T) / 3600000.0;

            if (gapHours > expectedHours + 0.001)
            {
                var prevLocal = DateTimeOffset.FromUnixTimeMilliseconds(prev.T).ToOffset(TimeSpan.FromHours(timezoneOffset));
                var currLocal = DateTimeOffset.FromUnixTimeMilliseconds(curr.T).ToOffset(TimeSpan.FromHours(timezoneOffset));

                bool anyUnexpected = false;
                string detail = "";

                var step = TimeSpan.FromHours(expectedHours);
                var t = prevLocal + step;
                while (t < currLocal)
                {
                    bool isClosed = false;
                    string kind = "";

                    if (skipWeekend && (t.DayOfWeek == DayOfWeek.Saturday || t.DayOfWeek == DayOfWeek.Sunday))
                    {
                        isClosed = true;
                        kind = "Weekend";
                    }
                    else if (sessionClosedHours.Contains(t.Hour))
                    {
                        isClosed = true;
                        kind = "Session";
                    }

                    if (isClosed)
                    {
                        expectedClosures++;
                        if (closureKinds.ContainsKey(kind)) closureKinds[kind]++;
                    }
                    else
                    {
                        anyUnexpected = true;
                        if (string.IsNullOrEmpty(detail))
                            detail = $"Gap {gapHours:F0}h (expected {expectedHours:F0}h) — prevHour={prevLocal.Hour} currHour={currLocal.Hour}";
                    }

                    t += step;
                }

                if (anyUnexpected)
                {
                    problems.Add(new Problem("TimeUnexpected", currLocal.DateTime,
                        curr.O, curr.H, curr.L, curr.C, curr.V, detail));
                }
            }

            // Price gap: open differs from previous close by >1%
            if (i > 0)
            {
                double priceJump = Math.Abs(curr.O - prev.C) / prev.C;
                if (priceJump > 0.01)
                {
                    problems.Add(new Problem("PriceGap",
                        DateTimeOffset.FromUnixTimeMilliseconds(curr.T).ToOffset(TimeSpan.FromHours(timezoneOffset)).DateTime,
                        curr.O, curr.H, curr.L, curr.C, curr.V,
                        $"Jump {(priceJump * 100):F2}% from prev close {prev.C:F2}"));
                }
            }

            // Volume spike: current volume > 5x average of previous 20 bars
            if (i >= 20)
            {
                double avgVol = bars.Skip(i - 20).Take(20).Average(b => b.V);
                if (curr.V > avgVol * 5)
                {
                    problems.Add(new Problem("VolumeSpike",
                        DateTimeOffset.FromUnixTimeMilliseconds(curr.T).ToOffset(TimeSpan.FromHours(timezoneOffset)).DateTime,
                        curr.O, curr.H, curr.L, curr.C, curr.V,
                        $"Volume {curr.V:F0} vs avg {avgVol:F0}"));
                }
            }
        }

        var sb = new StringBuilder();
        sb.AppendLine("=== SUMMARY ===");
        sb.AppendLine($"Total bars               : {bars.Count}");
        sb.AppendLine($"Real problems            : {problems.Count}");
        sb.AppendLine($"Expected closures        : {expectedClosures}");
        sb.AppendLine();
        sb.AppendLine("Problems by category:");
        foreach (var g in problems.GroupBy(p => p.Category).OrderBy(g => g.Key))
            sb.AppendLine($"  {g.Key,-18} : {g.Count()}");
        sb.AppendLine();
        sb.AppendLine("=== FIRST 30 PROBLEMS ===");
        sb.AppendLine();
        sb.AppendLine("Category       TimeLocal              O       H       L       C     V Detail");
        sb.AppendLine("--------       ---------              -       -       -       -     - ------");
        foreach (var p in problems.Take(30))
            sb.AppendLine($"{p.Category,-14} {p.TimeLocal:yyyy-MM-dd HH:mm} {p.O,7:F2} {p.H,7:F2} {p.L,7:F2} {p.C,7:F2} {p.V,6:F0} {p.Detail}");
        sb.AppendLine();
        sb.AppendLine("Closures by kind:");
        foreach (var kv in closureKinds)
            sb.AppendLine($"  {kv.Key,-12} : {kv.Value}");

        return sb.ToString();
    }

    private sealed record Problem(string Category, DateTime TimeLocal, double O, double H, double L, double C, double V, string Detail);
}