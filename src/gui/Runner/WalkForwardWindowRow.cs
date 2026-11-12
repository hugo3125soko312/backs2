using System;
using System.Globalization;
using backs2.Core.WalkForward;
using System.Linq;
using backs2.Core.Optimizer;

namespace backs2.ViewModels;

public sealed class WalkForwardWindowRow
{
    public string BatchId { get; }
    public string StrategyName { get; }
    public string Symbol { get; }
    public string Timeframe { get; }
    public int WindowIndex { get; }
    public int IsStartBar { get; }
    public int IsEndBar { get; }
    public int OosStartBar { get; }
    public int OosEndBar { get; }
    public string ParameterSummary { get; }
    public double IsObjective { get; }
    public double OosObjective { get; }
    public double Degradation { get; }
    public int OosTradeCount { get; }
    public double OosNetPnl { get; }
    public double OosMaxDrawdown { get; }
    public double OosTerminalBalance { get; }
    public bool OosBeatNull { get; }
    public bool Success { get; }
    public string? Error { get; }
    public long DurationMs { get; }

    public WalkForwardWindowRow(WalkForwardBatch batch, WalkForwardWindowResult w)
    {
        BatchId = batch.Id;
        StrategyName = batch.StrategyName;
        Symbol = batch.Symbol;
        Timeframe = batch.Timeframe;
        WindowIndex = w.Index;
        IsStartBar = w.IsStartBar;
        IsEndBar = w.IsEndBar;
        OosStartBar = w.OosStartBar;
        OosEndBar = w.OosEndBar;
        ParameterSummary = FormatParams(w.ChosenParameters);
        IsObjective = w.IsObjective;
        OosObjective = w.OosObjective;
        Degradation = w.Degradation;
        OosTradeCount = w.OosTradeCount;
        OosNetPnl = w.OosNetPnl;
        OosMaxDrawdown = w.OosMaxDrawdown;
        OosTerminalBalance = w.OosTerminalBalance;
        OosBeatNull = w.OosBeatNull;
        Success = w.Success;
        Error = w.Error;
        DurationMs = w.DurationMs;
    }

    public string IsObjectiveDisplay => IsObjective.ToString("F4", CultureInfo.InvariantCulture);
    public string OosObjectiveDisplay => OosObjective.ToString("F4", CultureInfo.InvariantCulture);
    public string DegradationDisplay => $"{Degradation:F4}";
    public string OosNetPnlDisplay => OosNetPnl.ToString("F2", CultureInfo.InvariantCulture);
    public string OosMaxDrawdownDisplay => OosMaxDrawdown.ToString("F2", CultureInfo.InvariantCulture);
    public string OosTerminalBalanceDisplay => OosTerminalBalance.ToString("F2", CultureInfo.InvariantCulture);
    public string OosBeatNullDisplay => OosBeatNull ? "Yes" : "No";
    public string SuccessDisplay => Success ? "OK" : "Fail";
    public string DurationDisplay => $"{DurationMs} ms";
    public string Dataset => $"{Symbol}@{Timeframe}";

    public string WindowRange =>
        Success
            ? $"IS [{IsStartBar},{IsEndBar}) · OOS [{OosStartBar},{OosEndBar})"
            : "—";

    private static string FormatParams(ParameterSet[]? ps)
    {
        if (ps == null || ps.Length == 0) return "—";
        return string.Join(", ", ps.Select(p => $"{p.Name}={p.Value}"));
    }

    public double SortValue(string key) => key switch
    {
        "WindowIndex" => WindowIndex,
        "OosNetPnl" => OosNetPnl,
        "OosMaxDrawdown" => OosMaxDrawdown,
        "OosTerminalBalance" => OosTerminalBalance,
        "OosTradeCount" => OosTradeCount,
        "IsObjective" => IsObjective,
        "OosObjective" => OosObjective,
        "Degradation" => Degradation,
        "Objective" => OosObjective,
        _ => OosNetPnl,
    };
}