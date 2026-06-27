namespace StyloExtract.Abstractions;

/// <summary>
/// A selector-chain candidate mined from the rule-observation corpus by
/// <c>EvolvedSelectorEmitter</c> (Phase 2 Task 8). One row in the
/// <c>evolved_selector_candidates</c> table.
///
/// Candidates are not applied directly. Task 9 wires them into the apply path
/// and records win/loss outcomes via <see cref="ITemplateIndex.RecordCandidateOutcomeAsync"/>;
/// Task 11 promotes high-reputation candidates into a host's active template.
///
/// The <see cref="Host"/> field carries the raw host on emission (so emitters
/// can dedupe per-host writes). Reads from a backing store typically return
/// the hashed form only — <see cref="Host"/> will be empty on values returned
/// by store reads, mirroring <see cref="TemplateObservation"/>.
/// </summary>
public sealed record EvolvedSelectorCandidate
{
    /// <summary>New Guid per emission.</summary>
    public required Guid CandidateId { get; init; }

    /// <summary>
    /// Raw host the candidate targets (e.g. "www.example.com"). Persisted as
    /// an HMAC hash; not recoverable on read. Empty string on instances
    /// returned by the store.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Pre-computed HMAC bytes for <see cref="Host"/>. When non-empty the
    /// store writes these bytes verbatim into <c>host_hash</c> and skips
    /// rehashing <see cref="Host"/>. The mining emitter passes the hash
    /// directly when it doesn't have the raw host (bucket-scoped corpus
    /// reads). Empty array = let the store hash <see cref="Host"/>.
    /// </summary>
    public byte[] HostHash { get; init; } = Array.Empty<byte>();

    /// <summary>Cluster the candidate was mined from.</summary>
    public required int LshBucket { get; init; }

    /// <summary>Block role the candidate targets.</summary>
    public required BlockRole Role { get; init; }

    /// <summary>
    /// The mined identity-claim chain. Outermost-first ancestors; the last
    /// element is the leaf target. Same convention as <see cref="BlockRule.Claims"/>.
    /// </summary>
    public required IReadOnlyList<IdentityClaim> Claims { get; init; }

    /// <summary>
    /// xxHash64 of the leaf claim's identifying surface (tag ^ id). Combined
    /// with <see cref="Host"/> and <see cref="Role"/> it forms the dedup key
    /// for repeat emissions of the same anchor.
    /// </summary>
    public required ulong TargetSignature { get; init; }

    /// <summary>Observations that contributed to this candidate for this host.</summary>
    public required int SourceCount { get; init; }

    /// <summary>
    /// Minimum presence ratio the mining pass used (e.g. 0.7 = 70% of cluster
    /// members had to share an attribute for it to enter the chain).
    /// </summary>
    public required double Confidence { get; init; }

    /// <summary>
    /// Apply-time win/loss accumulator. Written by Task 9, read by Task 11.
    /// Defaults to 0 on emission.
    /// </summary>
    public int ReputationScore { get; init; }

    /// <summary>Last apply-time win. Null until the candidate has ever won.</summary>
    public DateTimeOffset? LastWonAt { get; init; }

    /// <summary>Last apply-time loss. Null until the candidate has ever lost.</summary>
    public DateTimeOffset? LastLostAt { get; init; }

    /// <summary>UTC emission timestamp.</summary>
    public required DateTimeOffset CreatedAt { get; init; }
}
