namespace StyloExtract.Streaming;

/// <summary>
/// Whole-buffer byte-pattern scanner — ref struct, stack-allocated state.
/// Used by <see cref="StreamingPathSelector"/> for synchronous one-shot
/// scans where the full response is already in memory.
///
/// Returns <see cref="ScanVerdict.Continue"/> when the input ended without
/// the FSM reaching a terminal state. Callers that own the "no more bytes
/// coming" contract (the single-pass selector, the chunked scanner's
/// Flush()) should latch that to <see cref="ScanVerdict.Bailout"/>.
///
/// Behaviour is identical to <see cref="IncrementalBytePatternScanner"/>
/// fed in one chunk; they share <see cref="ScannerCore"/> for the FSM and
/// pattern-matching logic.
/// </summary>
public ref struct BytePatternScanner
{
    private readonly StreamingTemplate _template;
    private ScannerCore.State _state;

    public BytePatternScanner(in StreamingTemplate template)
    {
        _template = template;
        _state = ScannerCore.State.Initial;
    }

    public readonly FenceState State => _state.Fence;
    public readonly long CaptureStartByte => _state.CaptureStartByte;
    public readonly long CaptureEndByte => _state.CaptureEndByte;

    public ScanVerdict Feed(ReadOnlySpan<byte> input) =>
        ScannerCore.Step(input, ref _state, in _template, out _, isFinalChunk: true);
}
