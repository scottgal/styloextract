namespace StyloExtract.Streaming;

/// <summary>
/// Stateful, heap-backed companion to <see cref="FenceScanner"/>. Combines
/// <see cref="IncrementalHtmlTokenizer"/> with a per-fence MinHash sketch that
/// survives chunk boundaries.
///
/// Usage:
/// <code>
/// var scanner = IncrementalFenceScanner.Create(template);
/// foreach (var chunk in chunks)
/// {
///     var verdict = scanner.Feed(chunk);
///     if (verdict is ScanVerdict.Captured or ScanVerdict.Bailout) break;
/// }
/// var final = scanner.Flush();
/// </code>
///
/// Architectural note: <see cref="FenceScanner"/> is a <c>ref struct</c> (so
/// fields can be <c>Span&lt;T&gt;</c> backed by the call-site's stack). That
/// shape can't survive an <c>await</c> or live as a field on a class, so
/// <see cref="IncrementalFenceScanner"/> replicates the same tick logic over
/// heap-allocated signature/window arrays. The behavioural contract is
/// identical: feeding the same bytes that <see cref="StreamingPathSelector.ScanByHost"/>
/// would scan in one shot yields the same verdict.
/// </summary>
public sealed class IncrementalFenceScanner
{
    private const int MaxWindowSize = 64;

    private readonly StreamingTemplate _template;
    private readonly ulong _combinedAllowlistBloom;
    private readonly IncrementalHtmlTokenizer _tokenizer;

    private readonly uint[] _signature;
    private readonly EventSlot[] _window;
    private int _sketchCount;
    private int _sketchWriteIdx;

    private FenceState _state;
    private long _bytesConsumed;
    private long _captureStartByte;
    private long _captureEndByte;
    private int _eventsSinceStateChange;
    private ScanVerdict _latched;

    private IncrementalFenceScanner(StreamingTemplate template)
    {
        if (template.WindowSize <= 0 || template.WindowSize > MaxWindowSize)
            throw new InvalidOperationException(
                $"Template {template.TemplateId} has invalid WindowSize {template.WindowSize}; " +
                $"must be 1..{MaxWindowSize}.");

        _template = template;
        _combinedAllowlistBloom =
            template.PrefixFence.TagAllowlistBloom |
            template.ContentStartFence.TagAllowlistBloom |
            template.ContentEndFence.TagAllowlistBloom;
        _tokenizer = new IncrementalHtmlTokenizer();
        _signature = new uint[RollingSketch.SignatureSize];
        _window = new EventSlot[template.WindowSize];
        Array.Fill(_signature, uint.MaxValue);
        _state = FenceState.AwaitPrefix;
        _latched = ScanVerdict.Continue;
    }

    /// <summary>Build a fresh incremental scanner for <paramref name="template"/>.</summary>
    public static IncrementalFenceScanner Create(StreamingTemplate template) => new(template);

    public FenceState State => _state;
    public long CaptureStartByte => _captureStartByte;
    public long CaptureEndByte => _captureEndByte;

    /// <summary>
    /// Feed the next response chunk into the scanner. Returns the latched
    /// verdict: <see cref="ScanVerdict.Continue"/> while the scanner is still
    /// scanning, terminal (<see cref="ScanVerdict.Captured"/> /
    /// <see cref="ScanVerdict.Bailout"/>) once the scanner has decided.
    /// Subsequent feeds on a terminal verdict are no-ops.
    /// </summary>
    public ScanVerdict Feed(ReadOnlySpan<byte> chunk)
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout)
            return _latched;
        _tokenizer.Feed(chunk);
        return DrainTokens();
    }

    /// <summary>
    /// Drain any remaining buffered tokens without expecting more bytes.
    /// Useful at end-of-stream when callers want to coerce a final verdict
    /// out of trailing buffered tags. Returns the latched verdict.
    /// </summary>
    public ScanVerdict Flush()
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout)
            return _latched;
        return DrainTokens();
    }

    private ScanVerdict DrainTokens()
    {
        while (_tokenizer.TryReadTag(out var evt))
        {
            var v = Tick(in evt);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                return v;
            }
        }
        return _latched; // Continue
    }

    /// <summary>
    /// Per-tag-event scoring identical to <see cref="FenceScanner.Tick"/> but
    /// over our heap-backed sketch state. Kept side-by-side with the ref-struct
    /// implementation so both paths stay in lockstep; any drift between them
    /// is a correctness bug surface.
    /// </summary>
    private ScanVerdict Tick(in TagEvent evt)
    {
        if (_state == FenceState.Captured) return ScanVerdict.Captured;
        if (_state == FenceState.Bailed) return ScanVerdict.Bailout;

        _bytesConsumed += evt.ByteLength;

        var bit = 1UL << (int)(evt.TagNameHash & 63);
        if ((_combinedAllowlistBloom & bit) != 0)
        {
            PushSketch(evt.TagNameHash, evt.ClassHash);
            RecomputeSketch();
            _eventsSinceStateChange++;

            var prevState = _state;
            switch (_state)
            {
                case FenceState.AwaitPrefix:
                    {
                        var f = _template.PrefixFence;
                        if (SketchMatches(in f))
                            _state = FenceState.AwaitContentStart;
                        break;
                    }
                case FenceState.AwaitContentStart:
                    {
                        var f = _template.ContentStartFence;
                        if (SketchMatches(in f))
                        {
                            _state = FenceState.Capturing;
                            _captureStartByte = _bytesConsumed;
                        }
                        break;
                    }
                case FenceState.Capturing:
                    {
                        var f = _template.ContentEndFence;
                        if (SketchMatches(in f))
                        {
                            _state = FenceState.Captured;
                            _captureEndByte = _bytesConsumed;
                            return ScanVerdict.Captured;
                        }
                        break;
                    }
            }
            if (_state != prevState) _eventsSinceStateChange = 0;
        }

        if (_state == FenceState.AwaitPrefix && _eventsSinceStateChange >= _template.MaxEventsWithoutTransition)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        if (_state == FenceState.Capturing && (_bytesConsumed - _captureStartByte) > _template.MaxCaptureBytes)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        if (_state == FenceState.AwaitPrefix && _bytesConsumed > _template.BailoutBytes)
        {
            _state = FenceState.Bailed;
            return ScanVerdict.Bailout;
        }

        return ScanVerdict.Continue;
    }

    private void PushSketch(ulong tagHash, ulong classHash)
    {
        _window[_sketchWriteIdx] = new EventSlot(tagHash, classHash);
        _sketchWriteIdx = (_sketchWriteIdx + 1) % _window.Length;
        _sketchCount++;
    }

    private void RecomputeSketch()
    {
        Array.Fill(_signature, uint.MaxValue);
        var populated = Math.Min(_sketchCount, _window.Length);
        if (populated == 0) return;

        Span<byte> buf = stackalloc byte[16];
        var seeds = GetSeeds();
        for (int i = 0; i < populated; i++)
        {
            var slot = _window[i];
            var shingle = ShingleHash(slot.TagHash, slot.ClassHash);
            BitConverter.TryWriteBytes(buf, shingle);
            for (int s = 0; s < RollingSketch.SignatureSize; s++)
            {
                BitConverter.TryWriteBytes(buf[8..], seeds[s]);
                var h = (uint)(System.IO.Hashing.XxHash64.HashToUInt64(buf) & 0xFFFFFFFFUL);
                if (h < _signature[s]) _signature[s] = h;
            }
        }
    }

    private bool SketchMatches(in TemplateFence fence)
    {
        Span<ulong> bands = stackalloc ulong[16];
        ComputeBands(_signature, bands);
        var fenceBands = fence.LshBands;
        var n = Math.Min(bands.Length, fenceBands.Length);
        for (int i = 0; i < n; i++)
            if (bands[i] == fenceBands[i]) return true;
        return false;
    }

    private static void ComputeBands(ReadOnlySpan<uint> signature, Span<ulong> bands)
    {
        const int rowsPerBand = 8;
        Span<byte> buf = stackalloc byte[rowsPerBand * 4];
        for (int b = 0; b < bands.Length; b++)
        {
            for (int r = 0; r < rowsPerBand; r++)
                BitConverter.TryWriteBytes(buf.Slice(r * 4, 4), signature[b * rowsPerBand + r]);
            bands[b] = System.IO.Hashing.XxHash64.HashToUInt64(buf);
        }
    }

    private static ulong ShingleHash(ulong tagHash, ulong classHash)
    {
        Span<byte> buf = stackalloc byte[16];
        BitConverter.TryWriteBytes(buf, tagHash);
        BitConverter.TryWriteBytes(buf[8..], classHash);
        return System.IO.Hashing.XxHash3.HashToUInt64(buf);
    }

    private static ReadOnlySpan<ulong> GetSeeds() => SeedsHolder.Seeds;

    private static class SeedsHolder
    {
        public static readonly ulong[] Seeds = BuildSeeds();
        private static ulong[] BuildSeeds()
        {
            var seeds = new ulong[RollingSketch.SignatureSize];
            for (int i = 0; i < RollingSketch.SignatureSize; i++)
                seeds[i] = 0x9E3779B97F4A7C15UL * (ulong)(i + 1);
            return seeds;
        }
    }
}
