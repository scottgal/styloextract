namespace StyloExtract.Streaming;

public interface IStreamingTemplateStore
{
    /// <summary>Hot-cache lookup — returns null on miss without touching the durable tier.</summary>
    StreamingTemplate? TryGetHot(Guid templateId);

    /// <summary>Async lookup including the durable tier; populates the hot cache on hit.</summary>
    ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default);

    /// <summary>Register a template — writes to the durable tier and the hot cache.</summary>
    ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default);
}
