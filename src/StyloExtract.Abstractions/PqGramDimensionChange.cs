namespace StyloExtract.Abstractions;

public sealed record PqGramDimensionChange
{
    public required string PqGramKey { get; init; }
    public required double OldCount { get; init; }
    public required double NewCount { get; init; }
}
