namespace StyloExtract.Abstractions;

public interface ITemplateIndex
{
    Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken);
    Task<Guid> RegisterAsync(byte[] hostHash, StructuralFingerprint fingerprint, LearnedExtractor extractor, CancellationToken cancellationToken);
    Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken);

    // -------- Phase 1 Task 5: append-only rule-observation corpus --------
    // These methods underpin the Phase 2 cross-host mining step. They are
    // ADDITIVE on top of the existing per-template storage above and never
    // read by the apply path. Default implementations are no-ops so custom
    // ITemplateIndex implementations can opt in incrementally.

    /// <summary>
    /// Append one inducer-decision audit record. Never fails on duplicate;
    /// observations are append-only. Default no-op — implementations may opt
    /// in by overriding.
    /// </summary>
    ValueTask AppendObservationAsync(TemplateObservation observation, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Query observations for one host. Optionally filter by role. Newest
    /// first. Default returns empty.
    /// </summary>
    ValueTask<IReadOnlyList<TemplateObservation>> GetObservationsByHostAsync(
        string host, BlockRole? role = null, int limit = 100, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<TemplateObservation>>(Array.Empty<TemplateObservation>());

    /// <summary>
    /// Query observations across hosts sharing one LSH bucket. Phase 2's
    /// primary cross-host mining query. Optionally filter by role. Newest
    /// first. Default returns empty.
    /// </summary>
    ValueTask<IReadOnlyList<TemplateObservation>> GetObservationsByBucketAsync(
        int lshBucket, BlockRole? role = null, int limit = 1000, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<TemplateObservation>>(Array.Empty<TemplateObservation>());

    /// <summary>
    /// Streaming iterator for large corpus walks. Optional bucket / role
    /// filters. Default yields nothing.
    /// </summary>
    IAsyncEnumerable<TemplateObservation> EnumerateObservationsAsync(
        int? lshBucket = null, BlockRole? role = null, CancellationToken cancellationToken = default)
        => EmptyAsyncEnumerable();

    private static async IAsyncEnumerable<TemplateObservation> EmptyAsyncEnumerable()
    {
        await Task.CompletedTask;
        yield break;
    }

    // -------- Phase 2 Task 8: evolved-selector candidates --------
    // EvolvedSelectorEmitter writes candidates here after mining a cluster;
    // Task 9's apply path reads them and reports outcomes via
    // RecordCandidateOutcomeAsync. Default implementations are no-op /
    // empty so non-SQLite backends don't break.

    /// <summary>
    /// Insert a mined selector candidate. Implementations dedupe on
    /// (host, role, target_signature) so repeat mining passes are
    /// idempotent. Default no-op.
    /// </summary>
    ValueTask UpsertCandidateAsync(EvolvedSelectorCandidate candidate, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;

    /// <summary>
    /// Read candidates emitted for one host, optionally scoped by role.
    /// Newest first. Default returns empty.
    /// </summary>
    ValueTask<IReadOnlyList<EvolvedSelectorCandidate>> GetCandidatesByHostAsync(
        string host, BlockRole? role = null, int limit = 100, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<EvolvedSelectorCandidate>>(Array.Empty<EvolvedSelectorCandidate>());

    /// <summary>
    /// Record an apply-time outcome for a candidate. Increments reputation on
    /// win, decrements on loss, and stamps the corresponding timestamp.
    /// Default no-op.
    /// </summary>
    ValueTask RecordCandidateOutcomeAsync(
        Guid candidateId, bool won, DateTimeOffset at, CancellationToken cancellationToken = default)
        => ValueTask.CompletedTask;
}
