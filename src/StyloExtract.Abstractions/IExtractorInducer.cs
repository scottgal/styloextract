using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IExtractorInducer
{
    LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks);

    /// <summary>
    /// Document-aware overload. When <paramref name="document"/> is non-null,
    /// implementations should re-resolve each block's XPath against the document
    /// to extract identity claims and emit an <see cref="IdentityClaim"/> ancestor
    /// chain alongside the legacy CSS-selector string. When null, falls back to
    /// the XPath-only emit path.
    ///
    /// Default implementation delegates to the document-less overload for
    /// back-compat with custom inducers that pre-date the identity-claim
    /// refactor (Phase 1 Task 2).
    /// </summary>
    LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks, IDocument? document) =>
        Induce(templateId, blocks);
}
