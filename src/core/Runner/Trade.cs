using System;

namespace backs2.Core.Runner;

public record Trade(
    DateTime EntryTime,
    DateTime ExitTime,
    int EntryIndex,
    int ExitIndex,
    double EntryPrice,
    double ExitPrice,
    double Size,
    string Side,
    double PnL,
    double PnLPercent
);