using System;
using System.Collections.Generic;

namespace backs2.Core.Diagnostics;

public sealed class PerfThresholds
{
    public double MinFps { get; init; } = 30.0;
    public double MinAvailableRamMb { get; init; } = 1024.0;
    public double MaxCpuPercent { get; init; } = 85.0;
    public double MaxGpuPercent { get; init; } = 95.0;
    public double VramBudgetFraction { get; init; } = 0.90;

    public string[] Breaches(PerfSnapshot s)
    {
        var hits = new List<string>(4);
        if (s.Fps is double fps && fps < MinFps) hits.Add("fps");
        if (s.AvailableRamMb < MinAvailableRamMb) hits.Add("ram");
        if (s.CpuPercent > MaxCpuPercent) hits.Add("cpu");
        if (s.GpuPercent is double gpu && gpu > MaxGpuPercent) hits.Add("gpu");
        if (s.VramUsedMb is double used && s.VramBudgetMb is double budget && budget > 0)
        {
            if (used / budget > VramBudgetFraction) hits.Add("vram");
        }
        return hits.ToArray();
    }
}