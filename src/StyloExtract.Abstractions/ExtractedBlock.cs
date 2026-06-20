namespace StyloExtract.Abstractions;

public sealed record ExtractedBlock
{
    public required string Id { get; init; }
    public required BlockRole Role { get; init; }
    public required double Confidence { get; init; }
    public required string Text { get; init; }
    public required string Markdown { get; init; }
    public required string XPath { get; init; }
    public string? CssSelector { get; init; }
    public required int TextLength { get; init; }
    public required double LinkDensity { get; init; }
    public required IReadOnlyList<ExtractedLink> Links { get; init; }
}
