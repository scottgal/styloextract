using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed record RefitResult(bool Refitted, int OldVersion, int NewVersion, LearnedExtractor? OldExtractor, LearnedExtractor? NewExtractor, StructuralFingerprint? OldFingerprint = null);

public sealed class RefitOrchestrator
{
    private readonly SqliteTemplateIndex _index;
    private readonly IExtractorInducer _inducer;
    private readonly double _driftThreshold;
    private readonly int _observationsBeforeStable;
    private readonly int _versionHistoryDepth;
    // EWMA smoothing factor for accumulated drift score (spec §7)
    private const double EwmaAlpha = 0.2;

    public RefitOrchestrator(SqliteTemplateIndex index, IExtractorInducer inducer,
        double driftRefitThreshold, int observationsBeforeStable, int versionHistoryDepth)
    {
        _index = index;
        _inducer = inducer;
        _driftThreshold = driftRefitThreshold;
        _observationsBeforeStable = observationsBeforeStable;
        _versionHistoryDepth = versionHistoryDepth;
    }

    public async Task<RefitResult> MaybeRefitAsync(Guid templateId, StructuralFingerprint currentFp,
        IReadOnlyList<ExtractedBlock> freshHeuristicBlocks, CancellationToken cancellationToken)
    {
        var existing = await _index.GetExtractorAsync(templateId, cancellationToken);
        if (existing is null) return new RefitResult(false, 0, 0, null, null);

        var obs = await _index.GetObservationCountAsync(templateId, cancellationToken);
        if (obs < _observationsBeforeStable) return new RefitResult(false, existing.Version, existing.Version, existing, null);

        var drift = DriftScorer.ScoreApplication(existing, freshHeuristicBlocks);

        // Spec §7: accumulate drift as EWMA over per-observation deltas.
        // Blend the single-call OverallDelta into the persisted accumulated score.
        var oldAccumulated = existing.Centroid.OverallDriftScore;
        var newAccumulated = EwmaAlpha * drift.OverallDelta + (1 - EwmaAlpha) * oldAccumulated;

        // Persist the updated accumulated drift score before checking threshold.
        if (Math.Abs(newAccumulated - oldAccumulated) > 1e-9)
        {
            var updatedCentroid = existing.Centroid with { OverallDriftScore = newAccumulated };
            var updatedExtractor = existing with { Centroid = updatedCentroid };
            await _index.UpdateExtractorAsync(templateId, updatedExtractor, cancellationToken);
            existing = updatedExtractor;
        }

        // Threshold on accumulated score, not the single-call value.
        if (newAccumulated < _driftThreshold)
        {
            return new RefitResult(false, existing.Version, existing.Version, existing, null);
        }

        var freshExtractor = _inducer.Induce(templateId, freshHeuristicBlocks) with { Version = existing.Version + 1 };
        // Reset accumulated drift score on refit (spec §7).
        var refitCentroid = freshExtractor.Centroid with { OverallDriftScore = 0 };
        freshExtractor = freshExtractor with { Centroid = refitCentroid };

        var (oldV, newV, oldFp) = await _index.BumpVersionAsync(templateId, freshExtractor, currentFp, "drift", _versionHistoryDepth, cancellationToken);
        return new RefitResult(true, oldV, newV, existing, freshExtractor, oldFp);
    }
}
