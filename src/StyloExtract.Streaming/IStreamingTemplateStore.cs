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
    /// Look up the LATEST streaming template registered for <paramref name="host"/>.
    /// Host string is exact-match — lowercase + canonical form before calling.
    /// Returns null if no template has been registered for this host.
    /// Goes through the hot cache first; falls through to durable storage.
    /// </summary>
    ValueTask<StreamingTemplate?> GetByHostAsync(string host, CancellationToken cancellationToken = default);

    /// <summary>
    /// Synchronous hot-cache-only host lookup (LATEST version). Returns null on
    /// miss; caller should fall back to <see cref="GetByHostAsync"/> to bring
    /// it into hot cache.
    /// </summary>
    StreamingTemplate? TryGetHotByHost(string host);

    /// <summary>
    /// Upsert a template — write-through to durable + hot cache, keyed by both
    /// <see cref="StreamingTemplate.TemplateId"/> and <see cref="StreamingTemplate.Host"/>.
    /// alpha.21: appends a new version per (host, version) — does NOT replace
    /// prior versions. <see cref="GetByHostAsync"/> always returns the latest;
    /// <see cref="GetByHostAtVersionAsync"/> retrieves earlier versions.
    /// </summary>
    ValueTask UpsertAsync(StreamingTemplate template, CancellationToken cancellationToken = default);

    /// <summary>
    /// Look up a SPECIFIC version of a host's template. Returns null if the
    /// host has no template at that version. alpha.21+.
    /// </summary>
    ValueTask<StreamingTemplate?> GetByHostAtVersionAsync(
        string host,
        int version,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// List all known versions for <paramref name="host"/> (ascending).
    /// Returns an empty list if the host is unknown. alpha.21+.
    /// </summary>
    ValueTask<IReadOnlyList<int>> ListVersionsByHostAsync(
        string host,
        CancellationToken cancellationToken = default);
}
