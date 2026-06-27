namespace StyloExtract.Streaming;

public sealed class StreamingPathSelector
{
    private readonly IStreamingTemplateStore _store;

    public StreamingPathSelector(IStreamingTemplateStore store)
    {
        _store = store;
    }

    /// <summary>
    /// Synchronous hot-path scan. Looks up the template via the store's hot cache only;
    /// returns NoTemplate on miss (caller should WarmAsync first if needed).
    /// </summary>
    public ScanVerdict Scan(Guid templateId, ReadOnlySpan<byte> html)
    {
        var template = _store.TryGetHot(templateId);
        if (template is null) return ScanVerdict.NoTemplate;
        return ScanCore(template, html);
    }

    /// <summary>
    /// Synchronous hot-path scan by host. Hot-cache only; returns NoTemplate on miss
    /// so the caller can <see cref="WarmByHostAsync"/> + retry, or kick auto-induction.
    /// </summary>
    public ScanVerdict ScanByHost(string host, ReadOnlySpan<byte> html)
    {
        var template = _store.TryGetHotByHost(host);
        if (template is null) return ScanVerdict.NoTemplate;
        return ScanCore(template, html);
    }

    /// <summary>
    /// Bring a template into the hot cache by going through the durable tier if needed.
    /// </summary>
    public async ValueTask<bool> WarmAsync(Guid templateId, CancellationToken cancellationToken = default) =>
        await _store.GetAsync(templateId, cancellationToken).ConfigureAwait(false) is not null;

    /// <summary>
    /// Bring a host's template into the hot cache by going through the durable tier.
    /// Returns true if a template was found and loaded; false on miss.
    /// </summary>
    public async ValueTask<bool> WarmByHostAsync(string host, CancellationToken cancellationToken = default) =>
        await _store.GetByHostAsync(host, cancellationToken).ConfigureAwait(false) is not null;

    private static ScanVerdict ScanCore(StreamingTemplate template, ReadOnlySpan<byte> html)
    {
        var scanner = new FenceScanner(in template);
        // Tag-hash prefilter: skip per-tag attribute extraction for tags whose
        // name-hash can't possibly satisfy any of the template's 3 tripwires.
        // ~95% of tags on a real page (every span/a/img/etc.) get rejected on
        // the FSM's tag-hash compare anyway — the extraction was pure waste.
        var filter = TripwireTagFilter.FromTemplate(in template);
        var tokenizer = new MinimalHtmlTokenizer(html, filter);

        var verdict = ScanVerdict.Continue;
        while (verdict == ScanVerdict.Continue && tokenizer.TryReadTag(out var evt))
            verdict = scanner.Tick(in evt);
        return verdict;
    }
}
