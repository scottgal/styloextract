namespace StyloExtract.Abstractions;

public sealed record TemplateVersionDiff
{
    public required IReadOnlyList<PqGramDimensionChange> TopChangedDimensions { get; init; }
    public required IReadOnlyList<BlockRule> AddedRules { get; init; }
    public required IReadOnlyList<BlockRule> RemovedRules { get; init; }
    public required IReadOnlyList<RuleSelectorChange> ChangedSelectors { get; init; }
    public required double SignatureJaccardDelta { get; init; }
}
