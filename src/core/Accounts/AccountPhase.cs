namespace backs2.Core.Accounts;

public sealed record AccountPhase(
    string PhaseName,
    double? ProfitTargetPercent,
    double? MaxLossPercent,
    double? MaxDailyLossPercent,
    int? MinTradingDays,
    int? TradingPeriodDays,
    double? BestDayRulePercent,
    double? ProfitSplitPercent,
    bool? Refundable
);