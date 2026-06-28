using System.Buffers;

namespace StyloExtract.Streaming;

/// <summary>
/// Stateful, heap-backed byte-pattern scanner that survives chunk boundaries.
/// Keeps a small carry-over buffer for the trailing fragment of a chunk that
/// couldn't be consumed in isolation — a partial open tag, a script body
/// whose close marker straddles the boundary, etc.
///
/// <para>Buffer contract: the carry-over buffer is rented from
/// <see cref="ArrayPool{Byte}.Shared"/> and grows on demand up to the
/// configured ceiling
/// <see cref="StreamingTokenizerOptions.MaxCarryBufferBytes"/> (default
/// 1 MiB). Under standards-conforming HTML the residual is bounded by
/// (longest pattern's MaxScanBytes + longest skip region's close marker),
/// comfortably below the default ceiling. <see cref="Feed"/> throws
/// <see cref="InvalidOperationException"/> only when input pushes past the
/// configured ceiling. <see cref="PeakBufferedBytes"/> reports the high
/// watermark for memory telemetry.</para>
///
/// <para>Dispose returns the rented buffer to the pool. Long-lived scanners
/// should be wrapped in <c>using</c>.</para>
///
/// Public surface mirrors the old <c>IncrementalFenceScanner</c>:
/// <see cref="Create"/>, <see cref="Feed"/>, <see cref="Flush"/>,
/// <see cref="State"/>, <see cref="CaptureStartByte"/>,
/// <see cref="CaptureEndByte"/>, <see cref="PeakBufferedBytes"/>,
/// <see cref="BytesConsumed"/>.
/// </summary>
public sealed class IncrementalBytePatternScanner : IDisposable
{
    private const int InitialBufferSize = 512;

    private readonly StreamingTemplate _template;
    private readonly int _maxCarryBufferBytes;
    private ScannerCore.State _state;
    private ScanVerdict _latched;
    private byte[] _buffer;
    private int _bufferLen;
    private int _peakBufferedBytes;

    private IncrementalBytePatternScanner(StreamingTemplate template, StreamingTokenizerOptions? options)
    {
        var opts = options ?? StreamingTokenizerOptions.Default;
        opts.Validate();
        _template = template;
        _maxCarryBufferBytes = opts.MaxCarryBufferBytes;
        _state = ScannerCore.State.Initial;
        _latched = ScanVerdict.Continue;
        _buffer = ArrayPool<byte>.Shared.Rent(InitialBufferSize);
    }

    public static IncrementalBytePatternScanner Create(StreamingTemplate template) => new(template, options: null);

    public static IncrementalBytePatternScanner Create(StreamingTemplate template, StreamingTokenizerOptions options) =>
        new(template, options);

    /// <summary>
    /// Configurable sanity ceiling on the carry-over buffer. Replaces the
    /// legacy public <c>const int MaxBufferSize</c>; tests assert against
    /// this property to bound the in-flight memory.
    /// </summary>
    public int MaxCarryBufferBytes => _maxCarryBufferBytes;

    public void Dispose()
    {
        var buf = _buffer;
        _buffer = null!;
        if (buf is not null) ArrayPool<byte>.Shared.Return(buf);
    }

    public FenceState State => _state.Fence;
    public long CaptureStartByte => _state.CaptureStartByte;
    public long CaptureEndByte => _state.CaptureEndByte;
    public int PeakBufferedBytes => _peakBufferedBytes;
    public long BytesConsumed => _state.TotalBytesConsumed;

    public ScanVerdict Feed(ReadOnlySpan<byte> chunk)
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout) return _latched;
        if (chunk.IsEmpty) { TrackPeak(); return _latched; }

        int chunkPos = 0;

        // Phase 1: drain any carry-over by progressively appending chunk
        // bytes to the buffer and stepping. Stop when the buffer empties
        // (so we can fast-path the remainder of chunk) or chunk is exhausted.
        while (_bufferLen > 0 && chunkPos < chunk.Length)
        {
            var headroom = _maxCarryBufferBytes - _bufferLen;
            if (headroom <= 0)
            {
                throw new InvalidOperationException(
                    $"IncrementalBytePatternScanner carry-over buffer is full at {_bufferLen} bytes " +
                    $"and no progress is being made. The active template's patterns can't match in the " +
                    $"available window; raise StreamingTokenizerOptions.MaxCarryBufferBytes if input is legitimate.");
            }
            var take = Math.Min(headroom, chunk.Length - chunkPos);
            EnsureBufferCapacity(_bufferLen + take);
            chunk.Slice(chunkPos, take).CopyTo(_buffer.AsSpan(_bufferLen));
            _bufferLen += take;
            chunkPos += take;

            var v = ScannerCore.Step(_buffer.AsSpan(0, _bufferLen), ref _state, in _template, out var consumed);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                TrackPeak();
                return v;
            }
            if (consumed > 0)
            {
                var residual = _bufferLen - consumed;
                if (residual > 0)
                    Buffer.BlockCopy(_buffer, consumed, _buffer, 0, residual);
                _bufferLen = residual;
            }
            else if (chunkPos >= chunk.Length)
            {
                // No progress and chunk is exhausted — carry over to next
                // Feed without throwing. Some carry-over situations need more
                // bytes to disambiguate.
                break;
            }
            // If consumed == 0 but chunk still has bytes, the loop will copy
            // more in next iteration. If headroom would hit 0 then, the
            // throw above fires.
        }

        // Phase 2: fast path — scan chunk's remainder in place.
        if (chunkPos < chunk.Length)
        {
            var remainder = chunk.Slice(chunkPos);
            var v = ScannerCore.Step(remainder, ref _state, in _template, out var consumed);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                TrackPeak();
                return v;
            }
            if (consumed < remainder.Length)
            {
                var tail = remainder.Length - consumed;
                EnsureBufferCapacity(tail);
                remainder.Slice(consumed, tail).CopyTo(_buffer);
                _bufferLen = tail;
            }
            TrackPeak();
            return v;
        }

        TrackPeak();
        return _latched;
    }

    public ScanVerdict Flush()
    {
        if (_latched is ScanVerdict.Captured or ScanVerdict.Bailout) return _latched;

        if (_bufferLen > 0)
        {
            var v = ScannerCore.Step(_buffer.AsSpan(0, _bufferLen), ref _state, in _template, out _, isFinalChunk: true);
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                _latched = v;
                TrackPeak();
                return v;
            }
            _bufferLen = 0;
        }

        _state.Fence = FenceState.Bailed;
        _latched = ScanVerdict.Bailout;
        TrackPeak();
        return ScanVerdict.Bailout;
    }

    private void EnsureBufferCapacity(int required)
    {
        if (required > _maxCarryBufferBytes)
        {
            throw new InvalidOperationException(
                $"IncrementalBytePatternScanner carry-over buffer would exceed {_maxCarryBufferBytes} bytes " +
                $"(required={required}). A pattern's MaxScanBytes or a skip region's close marker " +
                $"needs more carry than the configured ceiling allows; raise " +
                $"StreamingTokenizerOptions.MaxCarryBufferBytes if input is legitimate.");
        }
        if (required > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, required);
            if (newSize > _maxCarryBufferBytes) newSize = _maxCarryBufferBytes;
            var grown = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_buffer, 0, grown, 0, _bufferLen);
            ArrayPool<byte>.Shared.Return(_buffer);
            _buffer = grown;
        }
    }

    private void TrackPeak()
    {
        if (_bufferLen > _peakBufferedBytes) _peakBufferedBytes = _bufferLen;
    }
}
