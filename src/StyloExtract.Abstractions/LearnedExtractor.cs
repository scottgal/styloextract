namespace StyloExtract.Abstractions;

public sealed record LearnedExtractor
{
    public required Guid TemplateId { get; init; }
    public required int Version { get; init; }
    public required IReadOnlyList<BlockRule> Rules { get; init; }
    public required ExtractorCentroidState Centroid { get; init; }
}
