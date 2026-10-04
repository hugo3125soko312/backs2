using System;

namespace backs2.ViewModels.Messages;

/// <summary>
/// Broadcast when the user clicks a trade or cluster on the Monte Carlo
/// cluster plot. Receivers: MainViewModel (switch tab), ChartViewModel
/// (load dataset if needed and scroll to <see cref="TargetTime"/>).
/// </summary>
public sealed record NavigateToTradeMessage(
    string Symbol,
    string Timeframe,
    DateTime TargetTime);