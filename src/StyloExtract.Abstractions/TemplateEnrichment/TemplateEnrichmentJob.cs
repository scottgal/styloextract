namespace StyloExtract.Abstractions.TemplateEnrichment;

/// <summary>
/// One unit of work for the background <c>TemplateEnrichmentCoordinator</c>.
/// Enqueued by <c>LayoutExtractor</c> on the novel-template path, drained
/// out-of-band by the coordinator. The runtime hot path never blocks on
/// the LLM call this job represents.
///
/// <para>
/// The job carries the pre-rendered DOM skeleton (not the full HTML) so
/// the queue stays cheap to hold and the coordinator doesn't need to
/// re-parse the document. <c>FingerprintHex</c> is included for
/// observability — a coordinator implementation can correlate the
/// induced template back to the request that triggered it.
/// </para>
/// </summary>
public sealed record TemplateEnrichmentJob
{
    /// <summary>
    /// Hostname this enrichment targets. The induced template's
    /// <c>host</c> field is rewritten to this value if the model
    /// hallucinated a different one.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// The slim DOM representation that the LLM will see. Produced
    /// by <c>DomSkeletonRenderer</c> at enqueue time so the queue
    /// holds at most a few KB per job.
    /// </summary>
    public required string Skeleton { get; init; }

    /// <summary>
    /// Hex display string of the page's structural fingerprint. Used
    /// for observability + per-(host,template) cooldown bucketing;
    /// the coordinator can dedupe back-to-back jobs from the same
    /// page shape without re-inducing.
    /// </summary>
    public required string FingerprintHex { get; init; }

    /// <summary>
    /// When the producer enqueued this job. Used for queue ordering
    /// and age-based eviction (drop stale jobs older than N minutes
    /// because the cache they'd update has long since changed).
    /// </summary>
    public required DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    /// What the coordinator should do with this job.
    /// <see cref="EnrichmentJobKind.Induce"/> is the original case (no
    /// existing operator template, induce one from scratch).
    /// <see cref="EnrichmentJobKind.Repair"/> means an existing template
    /// produced poor output for this page; the coordinator loads the
    /// failing YAML from the operator-template root and asks the LLM
    /// to fix its selectors.
    /// </summary>
    public EnrichmentJobKind Kind { get; init; } = EnrichmentJobKind.Induce;

    /// <summary>
    /// For <see cref="EnrichmentJobKind.Repair"/>: a short sample of
    /// the bad Markdown the failing template produced. Included in the
    /// LLM prompt so the model can see the failure mode. Empty for
    /// induce jobs.
    /// </summary>
    public string BadMarkdownSample { get; init; } = string.Empty;
}

/// <summary>
/// Discriminates the two operations the enrichment coordinator handles.
/// </summary>
public enum EnrichmentJobKind
{
    /// <summary>No template exists yet; induce one from the page skeleton.</summary>
    Induce = 0,

    /// <summary>An existing template performed badly; ask the LLM to repair it.</summary>
    Repair = 1,
}
