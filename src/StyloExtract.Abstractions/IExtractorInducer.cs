namespace StyloExtract.Abstractions;

public interface IExtractorInducer
{
    LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks);
}
