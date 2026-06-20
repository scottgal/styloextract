namespace StyloExtract.Abstractions;

public sealed record ExtractionStats
{
    public required int BlockCount { get; init; }
    public required int FingerprintShingleCount { get; init; }
    public required TimeSpan ParseTime { get; init; }
    public required TimeSpan FingerprintTime { get; init; }
    public required TimeSpan MatchTime { get; init; }
    public required TimeSpan RenderTime { get; init; }
}
