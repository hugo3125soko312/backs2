using System.Collections.Generic;

namespace backs2.Core.Accounts;

public sealed record AccountConfig(
    string Name,
    string Firm,
    string Plan,
    string Currency,
    double StartingCapital,
    IReadOnlyList<AccountPhase> Phases,
    string? ConsistencyScoreRule,
    string? Notes
);