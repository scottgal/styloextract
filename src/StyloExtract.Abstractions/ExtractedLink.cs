namespace StyloExtract.Abstractions;

public sealed record ExtractedLink
{
    public required string Text { get; init; }
    public required string Href { get; init; }
    public required bool IsExternal { get; init; }
}
