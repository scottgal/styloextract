namespace StyloExtract.Abstractions;

public sealed record RoleCentroid
{
    public required int ObservationCount { get; init; }
    public required double MeanLinkDensity { get; init; }
    public required double MeanTextLength { get; init; }
    public required double MeanDepth { get; init; }
}
