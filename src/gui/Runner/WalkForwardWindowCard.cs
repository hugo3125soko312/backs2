using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Avalonia;

namespace backs2.ViewModels;

public sealed partial class WalkForwardWindowCard : ObservableObject
{
    public WalkForwardWindowRow Row { get; }

    public WalkForwardWindowCard(WalkForwardWindowRow row)
    {
        Row = row;
    }

    // Identity
    public string Title => $"W{Row.WindowIndex + 1}";

    // Dates (using bar indices for now)
    public string IsRangeText  => $"IS bars {Row.IsStartBar} -> {Row.IsEndBar}";
    public string OosRangeText => $"OOS bars {Row.OosStartBar} -> {Row.OosEndBar}";

    // Timeline geometry (0..1 fractions of total span)
    public double IsStartFrac  { get; init; }
    public double IsLenFrac    { get; init; }
    public double OosStartFrac { get; init; }
    public double OosLenFrac   { get; init; }

    // Big numbers
    public string NetPnlText   => Row.OosNetPnl.ToString("+#,0.##;-#,0.##;0");
    public string DrawdownText => $"{Row.OosMaxDrawdown:P1}";
    public string TradesText   => Row.OosTradeCount.ToString();
    public string ObjectiveText => $"{Row.IsObjective:F2} -> {Row.OosObjective:F2}";
    public string DegradationText => $"{Row.Degradation:P0}";

    // Health classification
    public string Health =>
        Row.OosNetPnl < 0 || Row.Degradation > 0.50 ? "bad" :
        Row.Degradation > 0.25                       ? "warn" : "good";

    // Sparkline points (normalised)
    public IReadOnlyList<Point> SparkPoints { get; init; } = Array.Empty<Point>();

    // Parameters text
    public string ParamsText { get; init; } = "";

    // Pin state
    [ObservableProperty] private bool _isPinned;
}
