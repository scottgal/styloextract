namespace StyloExtract.Streaming;

public ref struct FenceScanner
{
    private readonly StreamingTemplate _template;
    private readonly ulong _combinedAllowlistBloom;
    private RollingSketch _sketch;
    private FenceState _state;
    private long _bytesConsumed;
    private long _captureStartByte;
    private long _captureEndByte;
    private int _eventsSinceStateChange;

    public FenceScanner(in StreamingTemplate template, Span<uint> signature, Span<EventSlot> window)
    {
        _template = template;
        _combinedAllowlistBloom =
            template.PrefixFence.TagAllowlistBloom |
            template.ContentStartFence.TagAllowlistBloom |
            template.ContentEndFence.TagAllowlistBloom;
        _sketch = new RollingSketch(signature, window);
        _state = FenceState.AwaitPrefix;
        _bytesConsumed = 0;
        _captureStartByte = 0;
        _captureEndByte = 0;
        _eventsSinceStateChange = 0;
    }

    public readonly FenceState State => _state;
    public readonly long CaptureStartByte => _captureStartByte;
    public readonly long CaptureEndByte => _captureEndByte;

    public ScanVerdict Tick(in TagEvent evt)
    {
        if (_state == FenceState.Captured) return ScanVerdict.Captured;
        if (_state == FenceState.Bailed) return ScanVerdict.Bailout;

        _bytesConsumed += evt.ByteLength;

        // Bloom early-reject: most tags on a real page (meta/link/script chrome) aren't in
        // any fence's allowlist. Skip push + recompute entirely for those.
        var bit = 1UL << (int)(evt.TagNameHash & 63);
        if ((_combinedAllowlistBloom & bit) != 0)
        {
            _sketch.Push(evt.TagNameHash, evt.ClassHash);
            _sketch.Recompute();
            _eventsSinceStateChange++;

            var prevState = _state;
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
                    {
                        _state = FenceState.Capturing;
                        _captureStartByte = _bytesConsumed;
                    }
                    break;
                case FenceState.Capturing:
                    var contentEnd = _template.ContentEndFence;
                    if (_sketch.Matches(in contentEnd))
                    {
                        _state = FenceState.Captured;
                        _captureEndByte = _bytesConsumed;
                        return ScanVerdict.Captured;
                    }
                    break;
            }
            if (_state != prevState) _eventsSinceStateChange = 0;
        }

        // Structural drift bailout: too many accepted events without a state transition
        // means the MinHash isn't converging toward our fences — the template is wrong
        // for this page. Give up before walking the whole document.
        if (_state == FenceState.AwaitPrefix && _eventsSinceStateChange >= _template.MaxEventsWithoutTransition)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        // Capture-region byte cap: prevent runaway capture if content-end never matches.
        if (_state == FenceState.Capturing && (_bytesConsumed - _captureStartByte) > _template.MaxCaptureBytes)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        // Byte-budget hard cap (independent of drift detection).
        if (_state == FenceState.AwaitPrefix && _bytesConsumed > _template.BailoutBytes)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        return ScanVerdict.Continue;
    }
}
