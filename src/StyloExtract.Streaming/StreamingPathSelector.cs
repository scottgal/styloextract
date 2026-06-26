namespace StyloExtract.Streaming;

public sealed class StreamingPathSelector
{
    private readonly IStreamingTemplateStore _store;
    private readonly int _windowSize;

    public StreamingPathSelector(IStreamingTemplateStore store, int windowSize = 8)
    {
        _store = store;
        _windowSize = windowSize;
    }

    public ScanVerdict Scan(Guid templateId, ReadOnlySpan<byte> html)
    {
        var template = _store.Get(templateId);
        if (template is null) return ScanVerdict.NoTemplate;
        return ScanCore(template, html, _windowSize);
    }

    private static ScanVerdict ScanCore(StreamingTemplate template, ReadOnlySpan<byte> html, int windowSize)
    {
        Span<uint> signature = stackalloc uint[128];
        Span<EventSlot> window = stackalloc EventSlot[16];
        var actualWindow = window[..windowSize];

        var scanner = new FenceScanner(in template, signature, actualWindow);
        var tokenizer = new MinimalHtmlTokenizer(html);

        var verdict = ScanVerdict.Continue;
        while (verdict == ScanVerdict.Continue && tokenizer.TryReadTag(out var evt))
            verdict = scanner.Tick(in evt);
        return verdict;
    }
}
