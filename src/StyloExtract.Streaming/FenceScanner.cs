namespace StyloExtract.Streaming;

/// <summary>
/// Ref-struct, span-backed fence scanner — the hot whole-buffer path.
/// alpha.21: per-tick algorithm delegates to <see cref="StreamingTick.Step"/>,
/// the same static method the heap-backed
/// <see cref="IncrementalFenceScanner"/> calls.
/// </summary>
public ref struct FenceScanner
{
    private readonly StreamingTemplate _template;
    private readonly Span<uint> _signature;
    private readonly Span<EventSlot> _window;
    private StreamingTickState _state;

    public FenceScanner(in StreamingTemplate template, Span<uint> signature, Span<EventSlot> window)
    {
        _template = template;
        _signature = signature;
        _window = window;
        _signature.Fill(uint.MaxValue);
        _state = StreamingTickState.Initial;
    }

    public readonly FenceState State => _state.State;
    public readonly long CaptureStartByte => _state.CaptureStartByte;
    public readonly long CaptureEndByte => _state.CaptureEndByte;

    public ScanVerdict Tick(in TagEvent evt) =>
        StreamingTick.Step(in evt, ref _state, _signature, _window, in _template);
}
