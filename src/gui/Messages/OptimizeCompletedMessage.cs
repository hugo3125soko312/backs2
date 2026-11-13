namespace backs2.ViewModels.Messages;

public sealed record OptimizeCompletedMessage
{
    public int TotalCombinations { get; init; }
    public int EvaluatedCount { get; init; }
    public double BestEdgePercent { get; init; }
    public string BestParameters { get; init; } = "";
}
