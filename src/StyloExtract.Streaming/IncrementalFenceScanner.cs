namespace StyloExtract.Streaming;

/// <summary>
/// Stateful, heap-backed companion to <see cref="FenceScanner"/>. Combines
/// <see cref="IncrementalHtmlTokenizer"/> (sliding-window byte buffer that
/// retains only the partial tag in flight) with the tripwire FSM.
///
/// Task 4 (alpha.24): switched from MinHash fence matching to
/// <see cref="StreamingTick.Step"/> tripwire matching. The per-tick
/// algorithm lives in one static method shared with the ref-struct
/// <see cref="FenceScanner"/>; both paths now execute literally the same
/// instructions per event.
///
/// Public surface unchanged: <see cref="Create"/>, <see cref="Feed"/>,
/// <see cref="Flush"/>, <see cref="State"/>, <see cref="CaptureStartByte"/>,
/// <see cref="CaptureEndByte"/>, <see cref="PeakBufferedBytes"/>,
/// <see cref="BytesConsumed"/>. Downstream consumers (lucidVIEW FULL etc.)
/// see no API break.
/// </summary>
public sealed class IncrementalFenceScanner
{
    private readonly StreamingTemplate _template;
    private readonly IncrementalHtmlTokenizer _tokenizer;
    private StreamingTickState _state;
    private ScanVerdict _latched;

    private IncrementalFenceScanner(StreamingTemplate template)
    {
        _template = template;
        // Tag-hash prefilter (alpha.24 tuning): tokenizer skips per-tag
        // class/id/role/data/aria extraction for tags whose name-hash can't
        // possibly match the active tripwires. See TripwireTagFilter for
        // details.
        _tokenizer = new IncrementalHtmlTokenizer(TripwireTagFilter.FromTemplate(in template));
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
            var v = StreamingTick.Step(in evt, ref _state, in _template);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                return v;
            }
        }
        return _latched;
    }
}
