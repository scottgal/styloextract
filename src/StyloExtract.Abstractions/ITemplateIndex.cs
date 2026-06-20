namespace StyloExtract.Abstractions;

public interface ITemplateIndex
{
    Task<Guid?> ProbeFastPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<(Guid TemplateId, double Cosine)?> ProbeSlowPathAsync(byte[] hostHash, StructuralFingerprint fingerprint, double threshold, CancellationToken cancellationToken);
    Task<LearnedExtractor?> GetExtractorAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetObservationCountAsync(Guid templateId, CancellationToken cancellationToken);
    Task<int> GetTemplateVersionAsync(Guid templateId, CancellationToken cancellationToken);
    Task<Guid> RegisterAsync(byte[] hostHash, StructuralFingerprint fingerprint, LearnedExtractor extractor, CancellationToken cancellationToken);
    Task RecordObservationAsync(Guid templateId, StructuralFingerprint fingerprint, double similarity, CancellationToken cancellationToken);
}
