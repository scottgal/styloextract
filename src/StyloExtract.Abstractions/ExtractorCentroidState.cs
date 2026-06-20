namespace StyloExtract.Abstractions;

public sealed record ExtractorCentroidState
{
    public required int TotalObservations { get; init; }
    public required IReadOnlyDictionary<BlockRole, RoleCentroid> ByRole { get; init; }
    public required double OverallDriftScore { get; init; }
    public required DateTimeOffset LastObservation { get; init; }
}
