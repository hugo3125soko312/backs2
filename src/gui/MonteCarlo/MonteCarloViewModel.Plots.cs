using System;
using System.Collections.Generic;
using backs2.Core.Overview;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public partial class MonteCarloViewModel
{
    private const int MaxCurvesToPlot = 200;
    private const int MaxPointsPerCurve = 1000;

    private AvaPlot? _histPlot;
    private AvaPlot? _curvesPlot;

    private MonteCarloResult? _histRenderedFrom;
    private McPlotMode? _histRenderedMode;
    private MonteCarloResult? _curvesRenderedFrom;

    public void SetHistogramPlotControl(AvaPlot plot)
    {
        _histPlot = plot;
        ApplyPlotTheme(_histPlot);
        EnsureVisiblePlotRendered();
    }

    public void SetCurvesPlotControl(AvaPlot plot)
    {
        _curvesPlot = plot;
        ApplyPlotTheme(_curvesPlot);
        EnsureVisiblePlotRendered();
    }

    /// <summary>
    /// Renders whichever plot is currently visible if its cache marker
    /// does not match the current result + mode. Called on every toggle;
    /// the cache makes plain visibility flips a no-op.
    /// </summary>
    private void EnsureVisiblePlotRendered()
    {
        if (ShowEquityCurves)
        {
            var r = SelectedResult;
            if (r == null || _curvesPlot == null) return;
            if (_curvesRenderedFrom == r) return;
            RenderEquityCurves(r);
            _curvesRenderedFrom = r;
            return;
        }

        if (SelectedPlotMode == McPlotMode.Cluster)
        {
            var data = CurrentData;
            if (data == null || _clusterPlot == null) return;
            if (_clusterRenderedFrom == data) return;
            RenderClusterPlot(data);
            _clusterRenderedFrom = data;
            return;
        }

        var histR = SelectedResult;
        if (histR == null || _histPlot == null) return;
        if (_histRenderedFrom == histR && _histRenderedMode == SelectedPlotMode) return;

        if (SelectedPlotMode == McPlotMode.MaxDrawdown)
            RenderMaxDdHistogram(histR);
        else
            RenderPlots(histR);

        _histRenderedFrom = histR;
        _histRenderedMode = SelectedPlotMode;
    }

    private static void ApplyPlotTheme(AvaPlot? plot)
    {
        if (plot == null) return;
        var plt = plot.Plot;

        var labelColor = ScottPlot.Color.FromHex("#C8C8C8");
        var axisColor  = ScottPlot.Color.FromHex("#707070");

        plt.FigureBackground.Color = ScottPlot.Color.FromHex("#0F0F0F");
        plt.DataBackground.Color   = ScottPlot.Color.FromHex("#0F0F0F");

        plt.Axes.Color(axisColor);
        plt.Axes.Bottom.TickLabelStyle.ForeColor = labelColor;
        plt.Axes.Left.TickLabelStyle.ForeColor   = labelColor;
        plt.Axes.Right.TickLabelStyle.ForeColor  = labelColor;
        plt.Axes.Bottom.Label.ForeColor = labelColor;
        plt.Axes.Left.Label.ForeColor   = labelColor;
        plt.Axes.Right.Label.ForeColor  = labelColor;

        plt.Grid.MajorLineColor = ScottPlot.Color.FromHex("#262626");
        plt.Grid.MinorLineColor = ScottPlot.Color.FromHex("#1A1A1A");
    }

    private void RenderPlots(MonteCarloResult r)
    {
        if (_histPlot == null) return;
        if (r.TerminalBalances.Length == 0) return;

        var plt = _histPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_histPlot);

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(40, r.TerminalBalances);
        var barPlot = plt.Add.Bars(hist.Bins, hist.Counts);

        foreach (var bar in barPlot.Bars)
        {
            bar.FillColor = ScottPlot.Color.FromHex("#4FC3F7");
            bar.LineColor = ScottPlot.Colors.White;
            bar.LineWidth = 1;
        }

        plt.Add.VerticalLine(r.TerminalBalanceP05, 1, ScottPlot.Color.FromHex("#EF5350"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(r.TerminalBalanceMedian, 1, ScottPlot.Color.FromHex("#808080"), ScottPlot.LinePattern.Solid);
        plt.Add.VerticalLine(r.TerminalBalanceP95, 1, ScottPlot.Color.FromHex("#26A69A"), ScottPlot.LinePattern.Solid);

        plt.Axes.AutoScale();

        plt.Title("Terminal Balance Distribution");
        plt.Axes.Bottom.Label.Text = "Terminal Balance";
        plt.Axes.Left.Label.Text = "Frequency";

        ApplyPlotTheme(_histPlot);
        _histPlot.Refresh();
    }

    private void RenderMaxDdHistogram(MonteCarloResult r)
    {
        if (_histPlot == null) return;
        if (r.MaxDrawdowns.Length == 0) return;

        var plt = _histPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_histPlot);

        double limit = _lastMaxLossPercent;
        double p99 = ComputePercentile(r.MaxDrawdowns, 99.0);
        double xMin = 0;
        double xMax = Math.Max(p99 * 1.10, limit * 1.15);

        var hist = ScottPlot.Statistics.Histogram.WithBinCount(30, r.MaxDrawdowns);
        var barPlot = plt.Add.Bars(hist.Bins, hist.Counts);

        foreach (var bar in barPlot.Bars)
        {
            bar.FillColor = ScottPlot.Color.FromHex("#EF5350");
            bar.LineColor = ScottPlot.Colors.White;
            bar.LineWidth = 1;
        }

        int breaches = 0;
        for (int i = 0; i < r.MaxDrawdowns.Length; i++)
            if (r.MaxDrawdowns[i] >= limit) breaches++;
        double breachPct = (double)breaches / r.MaxDrawdowns.Length;

        plt.Add.VerticalLine(limit, 2, ScottPlot.Color.FromHex("#FFB74D"), ScottPlot.LinePattern.Dashed);
        plt.Add.VerticalLine(r.MaxDrawdownMedian, 1, ScottPlot.Color.FromHex("#808080"), ScottPlot.LinePattern.Solid);

        plt.Axes.AutoScale();
        plt.Axes.SetLimitsX(xMin, xMax);

        string title = breaches > 0
            ? $"Max Drawdown Distribution — {breachPct:P1} breach the {limit:0.##}% limit"
            : $"Max Drawdown Distribution — 0% breach the {limit:0.##}% limit";
        plt.Title(title);
        plt.Axes.Bottom.Label.Text = "Max Drawdown (%)";
        plt.Axes.Left.Label.Text = "Frequency";

        ApplyPlotTheme(_histPlot);
        _histPlot.Refresh();
    }

    private void RenderEquityCurves(MonteCarloResult r)
    {
        if (_curvesPlot == null) return;

        var plt = _curvesPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_curvesPlot);

        if (!r.CurvesRetained || r.EquityCurves.Length == 0)
        {
            plt.Title("Equity curves not retained for this run");
            plt.Axes.Bottom.Label.Text =
                $"Curves dropped above {MonteCarloEngine.MaxCurveCells / 1_000_000:N0}M cells " +
                $"({r.Iterations:N0} iter × {r.TargetDurationSlices:N0} slices). " +
                "Lower iterations to re-enable.";
            _curvesPlot.Refresh();
            return;
        }

        if (r.TargetDurationSlices < 2) return;

        var data = CurrentData;
        if (data?.SourceData == null || data.SourceData.Count < 2) return;

        double t0 = data.SourceData[0].Timestamp.ToOADate();
        double t1 = data.SourceData[data.SourceData.Count - 1].Timestamp.ToOADate();
        double step = (t1 - t0) / (r.TargetDurationSlices - 1);

        var xs = new double[r.TargetDurationSlices];
        for (int k = 0; k < r.TargetDurationSlices; k++)
            xs[k] = t0 + step * k;

        var (xsDown, _) = DownsampleAxis(xs, MaxPointsPerCurve);
        double sampleStep = xs.Length > MaxPointsPerCurve
            ? (double)xs.Length / xsDown.Length
            : 1.0;

        var indices = SelectCurveIndices(r, MaxCurvesToPlot);

        var red = ScottPlot.Color.FromHex("#EF5350").WithAlpha(0.35);
        var green = ScottPlot.Color.FromHex("#26A69A").WithAlpha(0.35);
        var blue = ScottPlot.Color.FromHex("#4FC3F7").WithAlpha(0.10);

        foreach (var i in indices)
        {
            var curve = r.EquityCurves[i];
            int elapsed = r.SlicesElapsed[i];
            if (elapsed < 2) continue;

            var ysDown = DownsampleValues(curve, elapsed, xsDown.Length, sampleStep);

            var line = plt.Add.ScatterLine(xsDown, ysDown);
            line.LineWidth = 1;
            line.MarkerSize = 0;
            line.Color = r.Outcomes[i] switch
            {
                MonteCarloOutcome.Blown => red,
                MonteCarloOutcome.Funded => green,
                _ => blue
            };
        }

        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();

        plt.Title($"Equity Curves — {indices.Count} of {r.EquityCurves.Length} paths");
        plt.Axes.Bottom.Label.Text = "Date";
        plt.Axes.Left.Label.Text = "Balance";

        ApplyPlotTheme(_curvesPlot);
        _curvesPlot.Refresh();
    }

    private static (double[] xs, int[] sourceIndices) DownsampleAxis(double[] xs, int maxPoints)
    {
        if (xs.Length <= maxPoints)
        {
            var identity = new int[xs.Length];
            for (int i = 0; i < xs.Length; i++) identity[i] = i;
            return ((double[])xs.Clone(), identity);
        }

        int n = maxPoints;
        var outXs = new double[n];
        var srcIdx = new int[n];
        double stepD = (double)(xs.Length - 1) / (n - 1);

        for (int k = 0; k < n; k++)
        {
            int src = (int)Math.Round(k * stepD);
            if (src >= xs.Length) src = xs.Length - 1;
            outXs[k] = xs[src];
            srcIdx[k] = src;
        }

        return (outXs, srcIdx);
    }

    private static double[] DownsampleValues(double[] curve, int elapsed, int outLen, double step)
    {
        var result = new double[outLen];
        int upper = Math.Min(elapsed, curve.Length);

        for (int k = 0; k < outLen; k++)
        {
            int src = (int)Math.Round(k * step);
            if (src >= upper) src = upper - 1;
            if (src < 0) src = 0;
            result[k] = curve[src];
        }

        return result;
    }

    private static List<int> SelectCurveIndices(MonteCarloResult r, int max)
    {
        int n = r.EquityCurves.Length;
        var indices = new List<int>(Math.Min(max, n));

        for (int i = 0; i < n && indices.Count < max; i++)
        {
            if (r.Outcomes[i] != MonteCarloOutcome.Timeout)
                indices.Add(i);
        }

        if (indices.Count >= max) return indices;

        var timeouts = new List<int>();
        for (int i = 0; i < n; i++)
        {
            if (r.Outcomes[i] == MonteCarloOutcome.Timeout)
                timeouts.Add(i);
        }

        int remaining = max - indices.Count;
        if (timeouts.Count <= remaining)
        {
            indices.AddRange(timeouts);
        }
        else
        {
            double stepD = (double)timeouts.Count / remaining;
            for (int k = 0; k < remaining; k++)
                indices.Add(timeouts[(int)(k * stepD)]);
        }

        return indices;
    }

    private static double ComputePercentile(double[] values, double p)
    {
        if (values.Length == 0) return 0;
        if (values.Length == 1) return values[0];

        var sorted = (double[])values.Clone();
        Array.Sort(sorted);

        double rank = (p / 100.0) * (sorted.Length - 1);
        int lower = (int)Math.Floor(rank);
        int upper = (int)Math.Ceiling(rank);
        if (lower == upper) return sorted[lower];

        double w = rank - lower;
        return sorted[lower] * (1 - w) + sorted[upper] * w;
    }
}