namespace StyloExtract.Abstractions;

public sealed record ExtractionResult
{
    public required Uri? SourceUri { get; init; }
    public required string? Title { get; init; }
    public required LayoutMatch Match { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<ExtractedBlock> Blocks { get; init; }
    public required ExtractionStats Stats { get; init; }
}
