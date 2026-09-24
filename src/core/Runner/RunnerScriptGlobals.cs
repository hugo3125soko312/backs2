using System;
using System.Collections.Generic;
using backs2.Core.Metrics;
using backs2.Core.Models;

namespace backs2.Core.Runner;

public class RunnerScriptGlobals
{
    public MetricRegistry Engine { get; set; } = null!;
    public List<Kline> Data { get; set; } = null!;
    public Action<string> Log { get; set; } = _ => { };
    public TradeRecorder Trades { get; set; } = null!;
    public Dictionary<string, string> Params { get; set; } = new();
}