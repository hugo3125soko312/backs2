using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using backs2.Core.Accounts;
using CommunityToolkit.Mvvm.Messaging;
using backs2.Core.Models;
using backs2.ViewModels.Messages;
using ScottPlot;
using ScottPlot.WPF;

namespace backs2.ViewModels;

public partial class MonteCarloViewModel
{
    private readonly IClusterService _clusterService;
    private WpfPlot? _clusterPlot;
    private readonly CancellationTokenSource _cts = new();

    public MonteCarloViewModel(IClusterService clusterService)
    {
        _clusterService = clusterService;
    }

    public void SetClusterPlotControl(WpfPlot plot)
    {
        _clusterPlot = plot;
        ApplyPlotTheme(plot);
    }

    private void DrawClusterPlot(object data, List<TradeResult> trades, int[] assignments, int k)
    {
        if (_clusterPlot == null) return;

        var info = (DrawClusterInfo)data;
        var scatter = new ScatterPlot(info.Xs, info.Ys);
        scatter.MarkerSize = 3;
        scatter.MarkerShape = MarkerShape.Circle;

        var colors = new Dictionary<int, Color>
        {
            [0] = Color.FromHex("#26A69A"),
            [1] = Color.FromHex("#EF5350"),
            [2] = Color.FromHex("#4FC3F7"),
            [3] = Color.FromHex("#FFB74D"),
            [4] = Color.FromHex("#AB47BC"),
        };

        var assignmentsArray = assignments;

        _clusterPlot.Plot.Clear();
        _clusterPlot.Plot.Add(scatter);

        var centroidColors = new List<Color>();
        for (int i = 0; i < k; i++)
        {
            var points = trades.Where((_, j) => assignmentsArray[j] == i).ToList();
            if (points.Count == 0) continue;

            double meanX = points.Average(p => p.PnL);
            double meanY = points.Average(p => p.Equity);
            centroidColors.Add(colors[i]);

            _clusterPlot.Plot.Add.Scatter(points.Select(p => p.PnL).ToArray(), points.Select(p => p.Equity).ToArray(),
                markerSize: 2, markerColor: colors[i], label: $"C{i + 1}");
        }

        _clusterPlot.Plot.Add.Scatter(info.CentroidXs, info.CentroidYs, markerSize: 8, markerColor: colors[0], label: "Centroids");
        _clusterPlot.Plot.ShowLegend();
        _clusterPlot.Refresh();
    }

    public record DrawClusterInfo(double[] Xs, double[] Ys, double[] CentroidXs, double[] CentroidYs);

    private void CancelRun()
    {
        _cts.Cancel();
        _cts.Dispose();
        _cts.Dispose();
    }
}
