using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed record RefitResult(bool Refitted, int OldVersion, int NewVersion, LearnedExtractor? OldExtractor, LearnedExtractor? NewExtractor);

public sealed class RefitOrchestrator
{
    private readonly SqliteTemplateIndex _index;
    private readonly IExtractorInducer _inducer;
    private readonly double _driftThreshold;
    private readonly int _observationsBeforeStable;
    private readonly int _versionHistoryDepth;

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
        if (drift.OverallDelta < _driftThreshold)
        {
            return new RefitResult(false, existing.Version, existing.Version, existing, null);
        }

        var freshExtractor = _inducer.Induce(templateId, freshHeuristicBlocks) with { Version = existing.Version + 1 };
        var (oldV, newV) = await _index.BumpVersionAsync(templateId, freshExtractor, currentFp, "drift", _versionHistoryDepth, cancellationToken);
        return new RefitResult(true, oldV, newV, existing, freshExtractor);
    }
}
