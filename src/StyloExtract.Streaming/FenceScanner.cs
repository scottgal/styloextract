namespace StyloExtract.Streaming;

/// <summary>
/// Ref-struct, span-backed tripwire scanner. Task 4 (alpha.24) collapsed
/// the alpha.21..23 sketch / window buffers — tripwire matching against
/// the tokenizer's per-event hash data has no per-tick scratch state, so
/// the scanner now only carries a small <see cref="StreamingTickState"/>.
/// </summary>
public ref struct FenceScanner
{
    private readonly StreamingTemplate _template;
    private StreamingTickState _state;

    public FenceScanner(in StreamingTemplate template)
    {
        _template = template;
        _state = StreamingTickState.Initial;
    }

    public readonly FenceState State => _state.State;
    public readonly long CaptureStartByte => _state.CaptureStartByte;
    public readonly long CaptureEndByte => _state.CaptureEndByte;

    public ScanVerdict Tick(in TagEvent evt) =>
        StreamingTick.Step(in evt, ref _state, in _template);
}
