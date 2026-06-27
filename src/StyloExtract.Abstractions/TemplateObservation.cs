namespace StyloExtract.Abstractions;

/// <summary>
/// An append-only audit record of a single rule emitted by an inducer for a
/// host. One <see cref="TemplateObservation"/> is written for every
/// <see cref="BlockRule"/> that ends up in a persisted template.
///
/// The observations corpus is never read by the apply path. It is the input
/// to the Phase 2 cross-host mining step which walks observations by
/// <see cref="LshBucket"/> + <see cref="Role"/> to extract the stable
/// substructure shared across cluster members.
///
/// Records are immutable and never deleted. Append-only by contract — no
/// update API exists on <see cref="ITemplateIndex"/>.
/// </summary>
public sealed record TemplateObservation
{
    /// <summary>Unique id for this observation. New Guid per call.</summary>
    public required Guid ObservationId { get; init; }

    /// <summary>
    /// Pre-hash host string (e.g. "www.example.com"). Persisted in the store
    /// as an HMAC hash; this property carries the raw host for log/diag
    /// surfaces and tests. The store hashes it on insert using the same
    /// scheme used for the templates table's <c>host_hash</c> column.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// The HMAC bytes the store assigned to <see cref="Host"/> at append
    /// time. Populated on reads sourced from the store; empty array on
    /// observations constructed by callers before insert. Phase 2 mining
    /// uses this to group bucket-scoped observations by host without
    /// needing to recover the raw host (which the HMAC hides).
    /// </summary>
    public byte[] HostHash { get; init; } = Array.Empty<byte>();

    /// <summary>
    /// LSH cluster bucket derived from the active template's fingerprint at
    /// induction time. 0 ("unbucketed") when no template fingerprint exists
    /// yet for the host (first-ever visit); Phase 2 mining can backfill.
    /// </summary>
    public required int LshBucket { get; init; }

    /// <summary>Block role this rule targets.</summary>
    public required BlockRole Role { get; init; }

    /// <summary>
    /// The identity-claim ancestor chain for the rule's target. Last element
    /// is the leaf target; preceding entries are outermost-first ancestors.
    /// Persisted as JSON via the source-generated serializer context.
    /// </summary>
    public required IReadOnlyList<IdentityClaim> Claims { get; init; }

    /// <summary>
    /// xxHash64 of the leaf claim's tag + id + sorted classes — used for
    /// de-dup queries on the corpus. Same target shape across different hosts
    /// hashes identically.
    /// </summary>
    public required ulong TargetSignature { get; init; }

    /// <summary>
    /// Number of elements this rule matched at induction time (from the
    /// Task 52 classifier count). Used by Phase 2 mining to weight by
    /// cluster membership and to detect under-/over-fitting selectors.
    /// </summary>
    public required int Cardinality { get; init; }

    /// <summary>Confidence the inducer assigned to this rule.</summary>
    public required double Confidence { get; init; }

    /// <summary>UTC timestamp of the induction call.</summary>
    public required DateTimeOffset InducedAt { get; init; }

    /// <summary>Which inducer produced this rule.</summary>
    public required InducerKind InducerKind { get; init; }
}
