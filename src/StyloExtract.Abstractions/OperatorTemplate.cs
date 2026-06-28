namespace StyloExtract.Abstractions;

/// <summary>
/// An operator-authored template that hard-overrides the induction pipeline
/// for a given host. When an <see cref="IOperatorTemplateStore"/> returns a
/// template for the host of an incoming request, the layout extractor skips
/// fingerprinting and classification entirely and runs only the
/// <see cref="IExtractorApplicator"/> against this template's rules.
///
/// Surface YAML is one file per host (see
/// <c>docs/operator-templates-design.md</c>). This record is the post-parse
/// in-memory representation.
/// </summary>
public sealed record OperatorTemplate
{
    /// <summary>
    /// The hostname this template applies to (exact match; subdomain matching
    /// is intentionally out of scope for v1).
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Free-form description from the YAML's <c>description:</c> field.
    /// Surfaces in CLI listings and the audit log; never affects runtime behaviour.
    /// </summary>
    public string Description { get; init; } = "";

    /// <summary>
    /// Schema-version of the template document. Bumped when the YAML shape
    /// changes; readers can reject documents whose version they don't support.
    /// </summary>
    public int Version { get; init; } = 1;

    /// <summary>
    /// Ordered list of (role -&gt; selectors) rules. The extractor applicator
    /// walks this list in order, so put the most-specific rule first when two
    /// might match the same element.
    /// </summary>
    public required IReadOnlyList<OperatorTemplateRule> Rules { get; init; }

    /// <summary>
    /// True when this template came from the deterministic heuristic
    /// inducer's YAML sink (a <c>&lt;host&gt;-deterministic.yaml</c> file),
    /// false for hand-authored or LLM-induced operator templates. Consumers
    /// that need to distinguish "real" operator overrides from automatic
    /// audit snapshots (e.g. the TemplateEnrichmentCoordinator's
    /// induce-already-handled gate) read this flag. The YAML payload itself
    /// does not carry this — the loader sets it based on the file name when
    /// reading from disk.
    /// </summary>
    public bool IsDeterministic { get; init; }
}

public sealed record OperatorTemplateRule
{
    public required BlockRole Role { get; init; }

    /// <summary>
    /// CSS selectors to match against the document. Each rule may carry
    /// multiple selectors; the applicator emits one block per matched element.
    /// </summary>
    public required IReadOnlyList<string> Selectors { get; init; }

    /// <summary>
    /// Confidence to stamp on every block emitted for this rule. Surfaces in
    /// <see cref="ExtractedBlock.Confidence"/> for downstream consumers.
    /// </summary>
    public double Confidence { get; init; } = 1.0;

    /// <summary>
    /// Optional identity-claim ancestor chain anchoring this rule. Outermost
    /// first; the last entry is the leaf target. When non-null the loaded
    /// template carries the same shape <see cref="BlockRule.Claims"/> carries
    /// at induction time, so the operator-template path can run on the
    /// <see cref="IdentityClaimApplicator"/> instead of the CSS-string
    /// fallback. Null on legacy operator templates that pre-date the
    /// identity-claim apply path.
    /// </summary>
    public IReadOnlyList<IdentityClaim>? Claims { get; init; }
}
