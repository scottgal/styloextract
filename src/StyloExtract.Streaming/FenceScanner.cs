namespace StyloExtract.Streaming;

public ref struct FenceScanner
{
    private readonly StreamingTemplate _template;
    private RollingSketch _sketch;
    private FenceState _state;
    private long _bytesConsumed;

    public FenceScanner(in StreamingTemplate template, Span<uint> signature, Span<EventSlot> window)
    {
        _template = template;
        _sketch = new RollingSketch(signature, window);
        _state = FenceState.AwaitPrefix;
        _bytesConsumed = 0;
    }

    public readonly FenceState State => _state;

    public ScanVerdict Tick(in TagEvent evt)
    {
        if (_state == FenceState.Captured) return ScanVerdict.Captured;
        if (_state == FenceState.Bailed) return ScanVerdict.Bailout;

        _bytesConsumed += evt.ByteLength;
        _sketch.Push(evt.TagNameHash, evt.ClassHash);
        _sketch.Recompute();

        switch (_state)
        {
            case FenceState.AwaitPrefix:
                var prefix = _template.PrefixFence;
                if (_sketch.Matches(in prefix))
                    _state = FenceState.AwaitContentStart;
                break;
            case FenceState.AwaitContentStart:
                var contentStart = _template.ContentStartFence;
                if (_sketch.Matches(in contentStart))
                    _state = FenceState.Capturing;
                break;
            case FenceState.Capturing:
                var contentEnd = _template.ContentEndFence;
                if (_sketch.Matches(in contentEnd))
                {
                    _state = FenceState.Captured;
                    return ScanVerdict.Captured;
                }
                break;
        }

        if (_state == FenceState.AwaitPrefix && _bytesConsumed > _template.BailoutBytes)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        return ScanVerdict.Continue;
    }
}
