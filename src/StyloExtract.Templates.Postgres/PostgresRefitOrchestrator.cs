using StyloExtract.Abstractions;
using StyloExtract.Templates;

namespace StyloExtract.Templates.Postgres;

/// <summary>
/// PostgreSQL-backed counterpart to <see cref="RefitOrchestrator"/>.
///
/// Identical drift-detection logic (EWMA alpha = 0.2, spec 7) but typed to
/// <see cref="PostgresTemplateIndex"/> so that the extended operations
/// (<c>UpdateExtractorAsync</c>, <c>BumpVersionAsync</c>) are accessible without
/// breaking the public <see cref="ITemplateIndex"/> contract.
/// </summary>
public sealed class PostgresRefitOrchestrator
{
    private readonly PostgresTemplateIndex _index;
    private readonly IExtractorInducer _inducer;
    private readonly double _driftThreshold;
    private readonly int _observationsBeforeStable;
    private readonly int _versionHistoryDepth;
    private const double EwmaAlpha = 0.2;

    /// <summary>The configured drift threshold; exposed for gate predicates in callers.</summary>
    public double DriftRefitThreshold => _driftThreshold;

    public PostgresRefitOrchestrator(
        PostgresTemplateIndex index,
        IExtractorInducer inducer,
        double driftRefitThreshold,
        int observationsBeforeStable,
        int versionHistoryDepth)
    {
        _index = index;
        _inducer = inducer;
        _driftThreshold = driftRefitThreshold;
        _observationsBeforeStable = observationsBeforeStable;
        _versionHistoryDepth = versionHistoryDepth;
    }

    public async Task<RefitResult> MaybeRefitAsync(
        Guid templateId,
        StructuralFingerprint currentFp,
        IReadOnlyList<ExtractedBlock> freshHeuristicBlocks,
        CancellationToken cancellationToken)
    {
        var existing = await _index.GetExtractorAsync(templateId, cancellationToken).ConfigureAwait(false);
        if (existing is null) return new RefitResult(false, 0, 0, null, null);

        var obs = await _index.GetObservationCountAsync(templateId, cancellationToken).ConfigureAwait(false);
        if (obs < _observationsBeforeStable)
            return new RefitResult(false, existing.Version, existing.Version, existing, null);

        var drift = DriftScorer.ScoreApplication(existing, freshHeuristicBlocks);

        var oldAccumulated = existing.Centroid.OverallDriftScore;
        var newAccumulated = EwmaAlpha * drift.OverallDelta + (1 - EwmaAlpha) * oldAccumulated;

        if (Math.Abs(newAccumulated - oldAccumulated) > 1e-9)
        {
            var updatedCentroid = existing.Centroid with { OverallDriftScore = newAccumulated };
            var updatedExtractor = existing with { Centroid = updatedCentroid };
            await _index.UpdateExtractorAsync(templateId, updatedExtractor, cancellationToken).ConfigureAwait(false);
            existing = updatedExtractor;
        }

        if (newAccumulated < _driftThreshold)
            return new RefitResult(false, existing.Version, existing.Version, existing, null);

        var freshExtractor = _inducer.Induce(templateId, freshHeuristicBlocks) with { Version = existing.Version + 1 };
        var refitCentroid = freshExtractor.Centroid with { OverallDriftScore = 0 };
        freshExtractor = freshExtractor with { Centroid = refitCentroid };

        var (oldV, newV, oldFp) = await _index.BumpVersionAsync(
            templateId, freshExtractor, currentFp, "drift", _versionHistoryDepth, cancellationToken).ConfigureAwait(false);

        return new RefitResult(true, oldV, newV, existing, freshExtractor, oldFp);
    }
}
