namespace StyloExtract.Abstractions;

public sealed record LayoutMatch
{
    public required Guid? TemplateId { get; init; }
    public required int TemplateVersion { get; init; }
    public required string FingerprintHex { get; init; }
    public required MatchStatus Status { get; init; }
    public required double Similarity { get; init; }
    public required int ObservationCount { get; init; }
    public required TimeSpan LatencyMatch { get; init; }
    public required TimeSpan LatencyTotal { get; init; }
}
