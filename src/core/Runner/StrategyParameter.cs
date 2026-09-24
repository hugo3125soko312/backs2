// src/core/Runner/StrategyParameter.cs
using System;
using System.Collections.Generic;

namespace backs2.Core.Runner;

public sealed record StrategyParameter(
    string Name,
    string Type,
    string DefaultValue,
    IReadOnlyList<string> Options
);