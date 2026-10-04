namespace backs2.Core.Compute;

public sealed record JobProgress(
    string Phase,
    int SlicesElapsed,
    int TotalSlices,
    double PercentComplete);