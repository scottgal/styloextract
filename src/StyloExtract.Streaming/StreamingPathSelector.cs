namespace StyloExtract.Streaming;

public sealed class StreamingPathSelector
{
    private readonly IStreamingTemplateStore _store;

    public StreamingPathSelector(IStreamingTemplateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Synchronous hot-path scan. Looks up the template via the store's hot
    /// cache only; returns NoTemplate on miss (caller should WarmAsync first
    /// if needed).
    /// </summary>
    public ScanVerdict Scan(Guid templateId, ReadOnlySpan<byte> html)
    {
        var template = _store.TryGetHot(templateId);
        if (template is null) return ScanVerdict.NoTemplate;
        return ScanCore(template, html);
    }

    /// <summary>
    /// Synchronous hot-path scan by host. Hot-cache only; returns NoTemplate
    /// on miss so the caller can <see cref="WarmByHostAsync"/> + retry, or
    /// kick auto-induction.
    /// </summary>
    public ScanVerdict ScanByHost(string host, ReadOnlySpan<byte> html)
    {
        var template = _store.TryGetHotByHost(host);
        if (template is null) return ScanVerdict.NoTemplate;
        return ScanCore(template, html);
    }

    /// <summary>
    /// Bring a template into the hot cache by going through the durable tier
    /// if needed.
    /// </summary>
    public async ValueTask<bool> WarmAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        await _store.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Bring a host's template into the hot cache by going through the
    /// durable tier. Returns true if a template was found and loaded; false
    /// on miss.
    /// </summary>
    public async ValueTask<bool> WarmByHostAsync(string host, CancellationToken cancellationToken = default) =>
        await _store.GetByHostAsync(host, cancellationToken).ConfigureAwait(false) is not null;

    private static ScanVerdict ScanCore(StreamingTemplate template, ReadOnlySpan<byte> html)
    {
        var scanner = new BytePatternScanner(in template);
        var v = scanner.Feed(html);
        // Selector owns the "whole response, no more bytes coming" contract —
        // latch Continue at end-of-input to Bailout so callers can fall through
        // to the slow path. Matches the alpha.23 Flush() semantics.
        if (v == ScanVerdict.Continue) v = ScanVerdict.Bailout;
        return v;
    }
}
