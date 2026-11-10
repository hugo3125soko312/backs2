using System;
using System.Collections.Generic;

namespace backs2.Core.WalkForward;

/// <summary>
/// One window's bar ranges, in the [start, end) convention used by
/// <see cref="System.Collections.Generic.List{T}.GetRange"/>.
/// </summary>
public sealed record WalkForwardWindowPlan(
    int Index,
    int IsStartBar, int IsEndBar,
    int OosStartBar, int OosEndBar);

/// <summary>
/// Thrown when the config cannot produce a valid plan. The message is
/// user-facing (the VM shows it in StatusText) so it must state what to change.
/// </summary>
public sealed class WalkForwardPlanException : Exception
{
    public WalkForwardPlanException(string message) : base(message) { }
}

/// <summary>
/// Pure planning arithmetic: given a total bar count and a config, emit the
/// ordered list of windows (or throw <see cref="WalkForwardPlanException"/>).
///
/// <para>Rules, in order:
/// <list type="number">
///   <item>Resolve lengths to bars (Percent =&gt; round(total * pct / 100), min 1).</item>
///   <item>Reject isLen &lt; <see cref="MinIsBars"/>.</item>
///   <item>Reject step &lt; oosLen (overlapping OOS would make the stitched PnL lie).</item>
///   <item>Emit windows; stop when oosEnd &gt; totalBars.</item>
///   <item>Reject when the count is below <c>cfg.MinWindows</c>.</item>
/// </list></para>
/// </summary>
public static class WalkForwardWindowPlanner
{
    /// <summary>Minimum in-sample length, in bars. Below this the MC pass is meaningless.</summary>
    public const int MinIsBars = 100;

    /// <summary>Resolves a raw config to windows for a dataset of the given size.</summary>
    public static IReadOnlyList<WalkForwardWindowPlan> Plan(int totalBars, WalkForwardRunConfig cfg)
    {
        if (cfg == null) throw new ArgumentNullException(nameof(cfg));
        if (totalBars < 2) throw new WalkForwardPlanException("Dataset has no bars.");

        int isLen  = ResolveBars(cfg.IsLength,  cfg.LengthUnit, totalBars, "IS");
        int oosLen = ResolveBars(cfg.OosLength, cfg.LengthUnit, totalBars, "OOS");
        int step   = ResolveBars(cfg.StepLength, cfg.LengthUnit, totalBars, "Step");

        if (isLen < MinIsBars)
            throw new WalkForwardPlanException(
                $"IS length is {isLen} bar(s); minimum is {MinIsBars}.");

        if (step < oosLen)
            throw new WalkForwardPlanException(
                $"Step ({step}) is smaller than OOS length ({oosLen}); OOS windows would overlap.");

        bool anchored = string.Equals(cfg.WindowMode, "Anchored", StringComparison.OrdinalIgnoreCase);

        var windows = new List<WalkForwardWindowPlan>();
        for (int w = 0; ; w++)
        {
            int isStart, isEnd;
            if (anchored)
            {
                isStart = 0;
                isEnd   = isLen + w * step;
            }
            else
            {
                isStart = w * step;
                isEnd   = isStart + isLen;
            }

            int oosStart = isEnd;
            int oosEnd   = oosStart + oosLen;
            if (oosEnd > totalBars) break;

            windows.Add(new WalkForwardWindowPlan(w, isStart, isEnd, oosStart, oosEnd));
        }

        if (windows.Count < cfg.MinWindows)
        {
            int needed = isLen + oosLen + Math.Max(0, (cfg.MinWindows - 1) * step);
            throw new WalkForwardPlanException(
                $"Plan yields {windows.Count} window(s); minimum is {cfg.MinWindows}. " +
                $"At least {needed} bar(s) needed, dataset has {totalBars}.");
        }

        return windows;
    }

    /// <summary>
    /// Short one-line preview for the UI. Never throws: on a planning failure
    /// the exception message is returned so the VM can show it in red without
    /// a try/catch around a display-only call.
    /// </summary>
    public static string Describe(int totalBars, WalkForwardRunConfig cfg)
    {
        try
        {
            var plan = Plan(totalBars, cfg);
            var first = plan[0];
            return $"{plan.Count} window(s) \u00b7 " +
                   $"IS {first.IsEndBar - first.IsStartBar} bars \u00b7 " +
                   $"OOS {first.OosEndBar - first.OosStartBar} bars";
        }
        catch (WalkForwardPlanException ex)
        {
            return ex.Message;
        }
        catch (Exception ex)
        {
            return "Plan preview failed: " + ex.Message;
        }
    }

    private static int ResolveBars(double value, string unit, int totalBars, string label)
    {
        if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
            throw new WalkForwardPlanException($"{label} length must be a positive number.");

        bool percent = string.Equals(unit, "Percent", StringComparison.OrdinalIgnoreCase);
        double resolved = percent ? totalBars * value / 100.0 : value;
        int bars = (int)Math.Round(resolved, MidpointRounding.AwayFromZero);
        return Math.Max(1, bars);
    }
}