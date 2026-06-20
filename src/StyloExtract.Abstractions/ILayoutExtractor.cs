namespace StyloExtract.Abstractions;

public interface ILayoutExtractor
{
    Task<ExtractionResult> ExtractAsync(
        string html,
        Uri? sourceUri = null,
        ExtractionOptions? options = null,
        CancellationToken cancellationToken = default);
}
