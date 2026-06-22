using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

/// <summary>
/// Result of applying a learned extractor. Carries the produced blocks plus
/// rule-application stats so callers (LayoutExtractor) can detect a broken
/// cached extractor (catastrophic selector-miss or empty output) and bug out
/// to a fresh classify + refit before returning broken content.
/// </summary>
public sealed record ApplicatorResult(
    IReadOnlyList<ExtractedBlock> Blocks,
    int RulesApplied,
    int RulesMissed);

public interface IExtractorApplicator
{
    ApplicatorResult Apply(IDocument document, LearnedExtractor extractor);
}
