// src/core/Runner/Trade.cs
using System;

namespace backs2.Core.Runner;

public record Trade(
    DateTime EntryTime,
    DateTime ExitTime,
    double EntryPrice,
    double ExitPrice,
    string Side,
    double PnL,
    double PnLPercent
);