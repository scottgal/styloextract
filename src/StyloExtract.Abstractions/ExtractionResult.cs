namespace StyloExtract.Abstractions;

public sealed record ExtractionResult
{
    public required Uri? SourceUri { get; init; }
    public required string? Title { get; init; }
    public required LayoutMatch Match { get; init; }
    public required string Markdown { get; init; }
    public required IReadOnlyList<ExtractedBlock> Blocks { get; init; }
    public required ExtractionStats Stats { get; init; }

    /// <summary>
    /// True when the LLM template inducer was invoked (i.e. a
    /// <see cref="StyloExtract.Abstractions.TemplateEnrichment.TemplateEnrichmentJob"/>
    /// was successfully enqueued for this extraction). False for heuristic-only
    /// extractions, operator-template overrides, and hosts without the LLM stack
    /// wired. Defaults to false to preserve backwards compatibility.
    /// </summary>
    public bool LlmInductionFired { get; init; }
}
