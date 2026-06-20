namespace StyloExtract.Abstractions;

public sealed record BlockRule
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> CssSelectors { get; init; }
    public required double MeanConfidence { get; init; }
    public required int ObservationCount { get; init; }
    public required double DriftScore { get; init; }
}
