namespace StyloExtract.Abstractions;

/// <summary>
/// One row of output from <c>EvolvedSelectorEmitter.EmitForAllClustersAsync</c>:
/// the (LSH bucket, role) cell that was just mined and how many candidates
/// the pass upserted for it.
/// </summary>
public sealed record ClusterEmissionResult
{
    public required int LshBucket { get; init; }
    public required BlockRole Role { get; init; }
    public required int CandidatesEmitted { get; init; }
}
