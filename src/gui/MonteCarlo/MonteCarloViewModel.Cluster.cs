using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core.Analysis;
using backs2.Core.Overview;
using backs2.Core.Runner;
using backs2.ViewModels.Messages;
using ScottPlot.Avalonia;

namespace backs2.ViewModels;

public partial class MonteCarloViewModel
{
    private static readonly ScottPlot.Color[] ClusterPalette =
    {
        ScottPlot.Color.FromHex("#4FC3F7"),
        ScottPlot.Color.FromHex("#EF5350"),
        ScottPlot.Color.FromHex("#FFB74D"),
        ScottPlot.Color.FromHex("#AB47BC"),
        ScottPlot.Color.FromHex("#26A69A"),
        ScottPlot.Color.FromHex("#FF7043"),
        ScottPlot.Color.FromHex("#66BB6A"),
        ScottPlot.Color.FromHex("#EC407A")
    };

    private sealed record ClusterPoint(double X, double Y, int ClusterId, Trade Trade);
    private sealed record ClusterCentroid(double X, double Y, int ClusterId, int Size);

    private AvaPlot? _clusterPlot;
    private OverviewData? _clusterRenderedFrom;

    private OverviewData? _clusterData;
    private List<ClusterPoint>? _clusterPoints;
    private List<ClusterCentroid>? _clusterCentroids;

    public void SetClusterPlotControl(AvaPlot plot)
    {
        _clusterPlot = plot;
        ApplyPlotTheme(_clusterPlot);
        EnsureVisiblePlotRendered();
    }

    /// <summary>
    /// Fires off the clustering on a background thread. The cluster
    /// assignment is O(N × K × D × I) which is fast, but keeping it off
    /// the UI thread means a slow strategy with tens of thousands of
    /// trades does not block the toggle. Rendering happens on the UI
    /// thread in the callback.
    /// </summary>
    private void RenderClusterPlot(OverviewData data)
    {
        if (_clusterPlot == null) return;
        if (data.Trades.Count < 2) return;

        _clusterRenderedFrom = data;

        var trades = data.Trades;
        _ = Task.Run(() =>
        {
            int n = trades.Count;
            int k = Math.Max(3, Math.Min(8, (int)Math.Sqrt(n / 10.0)));
            var assignments = KMeansClusterer.Cluster(trades, k);
            Dispatcher.UIThread.Post(() => DrawClusterPlotOnUi(data, trades, assignments, k));
        });
    }

    private void DrawClusterPlotOnUi(OverviewData data, IReadOnlyList<Trade> trades, int[] assignments, int k)
    {
        if (_clusterPlot == null) return;
        if (data != CurrentData) return;

        var plt = _clusterPlot.Plot;
        plt.Clear();
        ApplyPlotTheme(_clusterPlot);

        int n = trades.Count;
        var points = new List<ClusterPoint>(n);

        var sumX = new double[k];
        var sumY = new double[k];
        var cnt = new int[k];

        for (int i = 0; i < n; i++)
        {
            var t = trades[i];
            double x = t.EntryTime.ToOADate();
            double y = t.PnLPercent * 100.0;
            int c = assignments[i];

            points.Add(new ClusterPoint(x, y, c, t));
            sumX[c] += x;
            sumY[c] += y;
            cnt[c]++;
        }

        for (int c = 0; c < k; c++)
        {
            int size = cnt[c];
            if (size == 0) continue;

            var xs = new double[size];
            var ys = new double[size];
            int idx = 0;
            for (int i = 0; i < n; i++)
            {
                if (assignments[i] != c) continue;
                xs[idx] = points[i].X;
                ys[idx] = points[i].Y;
                idx++;
            }

            var sc = plt.Add.Scatter(xs, ys);
            sc.Color = ClusterPalette[c % ClusterPalette.Length];
            sc.MarkerSize = 6;
            sc.MarkerShape = ScottPlot.MarkerShape.FilledCircle;
            sc.LineWidth = 0;
            sc.LegendText = $"Cluster {c + 1}  (n={size})";
        }

        var centroids = new List<ClusterCentroid>(k);
        for (int c = 0; c < k; c++)
        {
            if (cnt[c] == 0) continue;
            double cx = sumX[c] / cnt[c];
            double cy = sumY[c] / cnt[c];
            centroids.Add(new ClusterCentroid(cx, cy, c, cnt[c]));

            var marker = plt.Add.Scatter(new[] { cx }, new[] { cy });
            marker.Color = ClusterPalette[c % ClusterPalette.Length];
            marker.MarkerSize = 22;
            marker.MarkerShape = ScottPlot.MarkerShape.OpenDiamond;
            marker.MarkerLineWidth = 2;
            marker.LineWidth = 0;
            marker.LegendText = null;
        }

        plt.Axes.DateTimeTicksBottom();
        plt.Axes.AutoScale();

        plt.Title($"Trade Clusters — {n:N0} trades, {k} clusters (click to navigate)");
        plt.Axes.Bottom.Label.Text = "Entry time";
        plt.Axes.Left.Label.Text = "PnL (%)";

        ApplyPlotTheme(_clusterPlot);
        _clusterPlot.Refresh();

        _clusterData = data;
        _clusterPoints = points;
        _clusterCentroids = centroids;
    }

    /// <summary>
    /// Called by the view's PointerPressed handler. Finds the closest
    /// centroid (within a generous pixel threshold) or, failing that, the
    /// closest trade point, and issues a NavigateToTradeMessage so the
    /// Chart tab can jump.
    /// </summary>
    public void HandleClusterPlotClick(double pixelX, double pixelY)
    {
        if (_clusterPlot == null) return;
        if (_clusterData == null || _clusterPoints == null || _clusterPoints.Count == 0) return;

        const double centroidThresholdPx = 40.0;

        int bestCentroid = -1;
        double bestCentroidDistSq = centroidThresholdPx * centroidThresholdPx;

        if (_clusterCentroids != null)
        {
            for (int i = 0; i < _clusterCentroids.Count; i++)
            {
                var c = _clusterCentroids[i];
                var cCoords = new ScottPlot.Coordinates(c.X, c.Y);
                var cPixel = _clusterPlot.Plot.GetPixel(cCoords);
                double dx = cPixel.X - pixelX;
                double dy = cPixel.Y - pixelY;
                double d2 = dx * dx + dy * dy;
                if (d2 < bestCentroidDistSq)
                {
                    bestCentroidDistSq = d2;
                    bestCentroid = i;
                }
            }
        }

        if (bestCentroid >= 0)
        {
            var c = _clusterCentroids![bestCentroid];
            Trade? first = null;
            foreach (var p in _clusterPoints)
            {
                if (p.ClusterId != c.ClusterId) continue;
                if (first == null || p.Trade.EntryTime < first.EntryTime) first = p.Trade;
            }
            if (first != null)
            {
                NavigateTo(first);
                StatusText = $"Cluster {c.ClusterId + 1} (n={c.Size}) → first trade at {first.EntryTime:yyyy-MM-dd HH:mm}";
                return;
            }
        }

        ClusterPoint? nearest = null;
        double nearestD2 = double.MaxValue;
        foreach (var p in _clusterPoints)
        {
            var pCoords = new ScottPlot.Coordinates(p.X, p.Y);
            var pPixel = _clusterPlot.Plot.GetPixel(pCoords);
            double dx = pPixel.X - pixelX;
            double dy = pPixel.Y - pixelY;
            double d2 = dx * dx + dy * dy;
            if (d2 < nearestD2)
            {
                nearestD2 = d2;
                nearest = p;
            }
        }

        if (nearest != null)
        {
            NavigateTo(nearest.Trade);
            StatusText = $"Trade → {nearest.Trade.Side} @ {nearest.Trade.EntryTime:yyyy-MM-dd HH:mm} · " +
                         $"PnL {nearest.Trade.PnLPercent * 100.0:+0.00;-0.00}%";
        }
    }

    private void NavigateTo(Trade trade)
    {
        if (_clusterData == null) return;

        WeakReferenceMessenger.Default.Send(new NavigateToTradeMessage(
            _clusterData.Symbol,
            _clusterData.Timeframe,
            trade.EntryTime));
    }
}