namespace StyloExtract.Streaming;

public sealed class StreamingPathSelector
{
    private const int MaxWindowSize = 64;
    private readonly IStreamingTemplateStore _store;

    public StreamingPathSelector(IStreamingTemplateStore store)
    {
        _store = store;
    }

    public ScanVerdict Scan(Guid templateId, ReadOnlySpan<byte> html)
    {
        var template = _store.Get(templateId);
        if (template is null) return ScanVerdict.NoTemplate;
        if (template.WindowSize <= 0 || template.WindowSize > MaxWindowSize)
            throw new InvalidOperationException(
                $"Template {templateId} has invalid WindowSize {template.WindowSize}; must be 1..{MaxWindowSize}.");

        Span<uint> signature = stackalloc uint[128];
        Span<EventSlot> windowBuffer = stackalloc EventSlot[MaxWindowSize];
        var window = windowBuffer[..template.WindowSize];

        var scanner = new FenceScanner(in template, signature, window);
        var tokenizer = new MinimalHtmlTokenizer(html);

        var verdict = ScanVerdict.Continue;
        while (verdict == ScanVerdict.Continue && tokenizer.TryReadTag(out var evt))
            verdict = scanner.Tick(in evt);
        return verdict;
    }
}
