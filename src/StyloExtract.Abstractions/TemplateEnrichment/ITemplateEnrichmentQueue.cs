namespace StyloExtract.Abstractions.TemplateEnrichment;

/// <summary>
/// Back-pressure-aware queue between <c>LayoutExtractor</c> (the producer
/// that sees novel templates on the hot path) and the background
/// <c>TemplateEnrichmentCoordinator</c> (which drains the queue,
/// invokes the LLM, and persists the result).
///
/// <para>
/// Implementations decide their own deduplication and bounding strategy.
/// The default <c>InMemoryTemplateEnrichmentQueue</c> dedupes per-host
/// inside a configurable cooldown window and drops new enqueues silently
/// when the bounded channel is full (the runtime never blocks on a full
/// queue — the heuristic-induced template covers the request and the
/// next visit to the same host re-enqueues if cooldown has expired).
/// </para>
/// </summary>
public interface ITemplateEnrichmentQueue
{
    /// <summary>
    /// Try to enqueue <paramref name="job"/>. Returns <c>true</c> if the
    /// job was accepted, <c>false</c> if the implementation dropped it
    /// (per-host cooldown still active, queue full, etc.). Producers
    /// MUST NOT retry on <c>false</c>; the drop is intentional.
    /// </summary>
    ValueTask<bool> TryEnqueueAsync(TemplateEnrichmentJob job, CancellationToken cancellationToken = default);

    /// <summary>
    /// Yields jobs in submission order. The async enumerable completes
    /// only when the queue is shut down (typically via host lifetime
    /// stop). Consumers run this in a hosted background loop.
    /// </summary>
    IAsyncEnumerable<TemplateEnrichmentJob> DequeueAllAsync(CancellationToken cancellationToken = default);
}
