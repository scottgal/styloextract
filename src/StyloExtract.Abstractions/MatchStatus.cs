namespace StyloExtract.Abstractions;

public enum MatchStatus
{
    FastPathHit,
    SlowPathMatch,
    Novel,
    NovelEphemeral,
    Refit,
    /// <summary>
    /// An operator-authored template overrode the induction pipeline for this host.
    /// The classifier, fingerprinter, and template index never ran for this request;
    /// extraction came from <see cref="IOperatorTemplateStore"/> via a synthetic
    /// <see cref="LearnedExtractor"/>. See <c>docs/operator-templates-design.md</c>.
    /// </summary>
    OperatorOverride
}
