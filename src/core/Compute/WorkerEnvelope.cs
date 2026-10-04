namespace backs2.Core.Compute;

public static class WorkerMessageKind
{
    public const string Start    = "start";
    public const string Progress = "progress";
    public const string Result   = "result";
    public const string Error    = "error";
    public const string Cancel   = "cancel";
    public const string Ping     = "ping";
    public const string Pong     = "pong";
}

public sealed record WorkerEnvelope
{
    public string Kind { get; init; } = "";

    public JobConfig?   Config       { get; init; }
    public JobProgress? Progress     { get; init; }
    public WorkerResult? Result      { get; init; }
    public string?      ErrorMessage { get; init; }
}

/// <summary>
/// Phase 1: placeholder result. Phase 2 will add IReadOnlyList&lt;Trade&gt; Trades.
/// Phase 3 will add the MC summary record.
/// </summary>
public sealed record WorkerResult(
    int TradeCount,
    string Summary);