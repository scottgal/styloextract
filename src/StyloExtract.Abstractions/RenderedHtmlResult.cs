namespace StyloExtract.Abstractions;

public sealed record RenderedHtmlResult
{
    public required string Html { get; init; }
    public required Uri FinalUri { get; init; }
    public required int StatusCode { get; init; }
    public required TimeSpan FetchTime { get; init; }
    public string? Title { get; init; }
}
