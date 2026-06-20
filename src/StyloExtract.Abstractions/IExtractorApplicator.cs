using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IExtractorApplicator
{
    IReadOnlyList<ExtractedBlock> Apply(IDocument document, LearnedExtractor extractor);
}
