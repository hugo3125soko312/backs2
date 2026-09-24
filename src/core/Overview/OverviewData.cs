using System;
using System.Collections.Generic;
using backs2.Core.Models;
using backs2.Core.Runner;

namespace backs2.Core.Overview;

public sealed record OverviewData(
    string StrategyName,
    string StrategyPath,
    string Symbol,
    string Timeframe,
    DateTime RunUtc,
    TimeSpan Duration,
    IReadOnlyList<Trade> Trades,
    IReadOnlyList<Kline> SourceData,
    string? Error
);