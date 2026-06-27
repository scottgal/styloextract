namespace StyloExtract.Abstractions;

public sealed record BlockRule
{
    public required string RuleId { get; init; }
    public required BlockRole Role { get; init; }
    public required IReadOnlyList<string> CssSelectors { get; init; }
    public required double MeanConfidence { get; init; }
    public required int ObservationCount { get; init; }
    public required double DriftScore { get; init; }

    /// <summary>
    /// Identity-claim ancestor chain anchoring this rule. The last entry is the
    /// target element's claim; entries 0..N-2 are ancestor claims, outermost
    /// first. When non-null, the apply path can evaluate this chain via
    /// <see cref="IdentityClaimMatcher.Matches"/> instead of parsing
    /// <see cref="CssSelectors"/> strings.
    ///
    /// Null on records produced before Phase 1 Task 2 of the identity-claim
    /// refactor, and on rules induced from <see cref="ExtractedBlock"/> lists
    /// without an accompanying document. Existing persisted blobs round-trip
    /// cleanly because the property is optional.
    /// </summary>
    public IReadOnlyList<IdentityClaim>? Claims { get; init; } = null;
}
