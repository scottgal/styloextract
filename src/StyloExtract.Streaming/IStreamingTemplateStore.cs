namespace StyloExtract.Streaming;

public interface IStreamingTemplateStore
{
    /// <summary>Hot-cache lookup — returns null on miss without touching the durable tier.</summary>
    StreamingTemplate? TryGetHot(Guid templateId);

    /// <summary>Async lookup including the durable tier; populates the hot cache on hit.</summary>
    ValueTask<StreamingTemplate?> GetAsync(Guid templateId, CancellationToken cancellationToken = default);

    /// <summary>Register a template — writes to the durable tier and the hot cache.</summary>
    ValueTask RegisterAsync(StreamingTemplate template, CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up the streaming template registered for <paramref name="host"/>.
    /// Host string is exact-match — lowercase + canonical form before calling.
    /// Returns null if no template has been registered for this host.
    /// Goes through the hot cache first; falls through to durable storage.
    /// </summary>
    ValueTask<StreamingTemplate?> GetByHostAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous hot-cache-only host lookup. Returns null on miss; caller
    /// should fall back to <see cref="GetByHostAsync"/> to bring it into hot cache.
    /// </summary>
    StreamingTemplate? TryGetHotByHost(string host);

    /// <summary>
    /// Upsert a template — write-through to durable + hot cache, keyed by both
    /// <see cref="StreamingTemplate.TemplateId"/> and <see cref="StreamingTemplate.Host"/>.
    /// One template per host (latest wins). Use this instead of
    /// <see cref="RegisterAsync"/> when the template was produced for a known host
    /// (typical for auto-induction).
    /// </summary>
    ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default);
}
