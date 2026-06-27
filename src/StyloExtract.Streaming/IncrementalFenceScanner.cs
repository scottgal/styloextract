namespace StyloExtract.Streaming;

/// <summary>
/// Stateful, heap-backed companion to <see cref="FenceScanner"/>. Combines
/// <see cref="IncrementalHtmlTokenizer"/> (sliding-window byte buffer that
/// retains only the partial tag in flight) with a fixed-size sliding event
/// window + MinHash sketch that survives chunk boundaries.
///
/// alpha.21 architecture: the per-tick algorithm lives in
/// <see cref="StreamingTick.Step"/> as a static method over a
/// <see cref="StreamingTickState"/>. Both <see cref="FenceScanner"/> (the
/// ref-struct, span-backed path) and this class (heap-backed for async
/// use) build a <c>TickState</c> from their storage and call the same
/// <c>Step</c>. Cross-validation tests remain as insurance, but both paths
/// now execute literally the same instructions.
/// </summary>
public sealed class IncrementalFenceScanner
{
    private const int MaxWindowSize = 64;

    private readonly StreamingTemplate _template;
    private readonly IncrementalHtmlTokenizer _tokenizer;

    private readonly uint[] _signature;
    private readonly EventSlot[] _window;
    private StreamingTickState _state;
    private ScanVerdict _latched;

    private IncrementalFenceScanner(StreamingTemplate template)
    {
        if (template.WindowSize <= 0 || template.WindowSize > MaxWindowSize)
            throw new InvalidOperationException(
                $"Template {template.TemplateId} has invalid WindowSize {template.WindowSize}; " +
                $"must be 1..{MaxWindowSize}.");

        _template = template;
        _tokenizer = new IncrementalHtmlTokenizer();
        _signature = new uint[RollingSketch.SignatureSize];
        _window = new EventSlot[template.WindowSize];
        Array.Fill(_signature, uint.MaxValue);
        _state = StreamingTickState.Initial;
        _latched = ScanVerdict.Continue;
    }

    public static IncrementalFenceScanner Create(StreamingTemplate template) => new(template);

    public FenceState State => _state.State;
    public long CaptureStartByte => _state.CaptureStartByte;
    public long CaptureEndByte => _state.CaptureEndByte;
    public int PeakBufferedBytes => _tokenizer.PeakBufferedBytes;
    public long BytesConsumed => _tokenizer.BytesConsumed;

    public ScanVerdict Feed(ReadOnlySpan<byte> chunk)
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout)
            return _latched;
        _tokenizer.Feed(chunk);
        return DrainTokens();
    }

    public ScanVerdict Flush()
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout)
            return _latched;
        var v = DrainTokens();
        if (v is ScanVerdict.Captured or ScanVerdict.Bailout) return v;

        // alpha.23 end-of-stream Bailout: Continue at EOF is meaningless —
        // the consumer signalled stream exhaustion via Flush(). Latch to
        // Bailout so callers can fall through to the slow path / re-induce.
        _state.State = FenceState.Bailed;
        _latched = ScanVerdict.Bailout;
        return ScanVerdict.Bailout;
    }

    private ScanVerdict DrainTokens()
    {
        while (_tokenizer.TryReadTag(out var evt))
        {
            var v = StreamingTick.Step(in evt, ref _state, _signature, _window, in _template);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                return v;
            }
        }
        return _latched;
    }
}
