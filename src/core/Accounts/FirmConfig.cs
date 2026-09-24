using System.Collections.Generic;

namespace backs2.Core.Accounts;

public sealed record FirmConfig(
    string Firm,
    string Currency,
    string? Notes,
    IReadOnlyList<FirmPlan> Plans
);

public sealed record FirmPlan(
    string Plan,
    IReadOnlyList<PlanSize> Sizes
);

public sealed record PlanSize(
    string Size,
    double StartingCapital,
    IReadOnlyList<AccountPhase> Phases,
    string? ConsistencyScoreRule,
    string? Notes
);