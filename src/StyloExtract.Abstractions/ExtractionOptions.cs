namespace StyloExtract.Abstractions;

public sealed record ExtractionOptions
{
    public ExtractionProfile Profile { get; init; } = ExtractionProfile.RagFull;
    public bool LearnNewTemplates { get; init; } = true;
    public bool EmitDebugMetadata { get; init; }
    public string? HostOverride { get; init; }

    /// <summary>
    /// When true, after applying the cached template the extractor ALSO evaluates
    /// any evolved selector candidates that exist for the host's role rules,
    /// recording outcome (matched >= 1 element = win; matched 0 = loss) into the
    /// reputation columns of evolved_selector_candidates. Cached extraction output
    /// is returned to the caller unchanged regardless. Phase 2 Task 11 reads the
    /// reputation to decide active promotion.
    ///
    /// Default false — evaluation is opt-in until reputation gating is validated.
    /// Set per-call via ExtractionOptions or globally via the StyloExtractCore
    /// builder options EvaluateEvolvedCandidates flag.
    /// </summary>
    public bool EvaluateEvolvedCandidates { get; init; } = false;
}
