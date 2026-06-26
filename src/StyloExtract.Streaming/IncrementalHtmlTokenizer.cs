using System.IO.Hashing;

namespace StyloExtract.Streaming;

/// <summary>
/// Stateful HTML tokenizer that survives chunk boundaries with a TRUE
/// sliding-window byte buffer: only the bytes of the partial tag currently
/// in flight (plus the small slack required to detect closing markers
/// straddling a chunk boundary) are retained. Once <see cref="TryReadTag"/>
/// emits an event, the bytes that produced it are dropped immediately —
/// the buffer's valid-byte count post-emit is O(partial-tag), never
/// O(response-size).
///
/// This is the alpha.19 redesign of the alpha.18 tokenizer, which held a
/// growable 8 KiB → 1 MiB buffer and only compacted at the next
/// <see cref="Feed"/> call. The new contract: the worst-case in-flight
/// buffer is the longest single tag (or script/style body close-marker
/// preserve window), so a streaming gateway can scan multi-megabyte
/// responses while holding bounded memory.
///
/// Behavioural contract (unchanged from alpha.18): feeding the same bytes
/// that <see cref="MinimalHtmlTokenizer"/> would consume — in any chunking
/// — yields the same <see cref="TagEvent"/> sequence in the same order,
/// with the same hashes and byte lengths. The stateful path is structurally
/// equivalent to the span path, just resumable.
///
/// Buffer growth: hard-capped at <see cref="MaxBufferSize"/> (64 KiB). The
/// cap is a safety stop that should never be hit under correct input; if a
/// single tag is genuinely longer than 64 KiB (or a script/style body has
/// no close-marker in 64 KiB of body), <see cref="Feed"/> throws
/// <see cref="InvalidOperationException"/> rather than silently dropping
/// bytes. <see cref="PeakBufferedBytes"/> exposes the high-watermark for
/// memory telemetry.
/// </summary>
public sealed class IncrementalHtmlTokenizer
{
    /// <summary>
    /// Hard safety cap on the internal byte buffer. Under correct input the
    /// buffer's valid-byte count post-emit is O(longest tag), so this cap
    /// is only hit on pathological input (a single tag > 64 KiB, or a
    /// script/style body with no closing marker in 64 KiB of body).
    /// </summary>
    public const int MaxBufferSize = 64 * 1024;

    private const int InitialBufferSize = 4 * 1024;

    private static readonly ulong s_scriptHash = XxHash3.HashToUInt64("script"u8);
    private static readonly ulong s_styleHash = XxHash3.HashToUInt64("style"u8);

    private byte[] _buffer;
    private int _filled;     // index past the last byte fed into _buffer
    private int _consumed;   // index of the next byte the tokenizer will look at
    private long _droppedBytes; // bytes dropped during compaction (kept so BytesConsumed monotonically grows)
    private int _peakBufferedBytes;

    // Skip-state: when we open a <script> or <style>, subsequent bytes belong to
    // the body of that element and must be skipped until the matching close tag
    // appears. The skip can span chunk boundaries — store the target close-tag
    // here so successive Feed calls can resume the scan.
    private byte[]? _pendingSkipTarget;

    public IncrementalHtmlTokenizer()
    {
        _buffer = new byte[InitialBufferSize];
    }

    /// <summary>
    /// Total bytes consumed by emitted <see cref="TagEvent"/>s (plus inter-tag
    /// text). Monotonically increases across <see cref="Feed"/> calls; usable
    /// as a byte offset analogue to <see cref="MinimalHtmlTokenizer"/>'s
    /// position.
    /// </summary>
    public long BytesConsumed => _droppedBytes + _consumed;

    /// <summary>
    /// High-watermark of the buffer's valid-byte count (<c>_filled - _consumed</c>)
    /// observed since construction. Diagnostic for memory telemetry: under the
    /// sliding-window contract, this should stay bounded by the longest tag
    /// observed plus one chunk's worth of slack, regardless of response size.
    /// </summary>
    public int PeakBufferedBytes => _peakBufferedBytes;

    /// <summary>
    /// Append <paramref name="chunk"/> to the internal buffer so subsequent
    /// <see cref="TryReadTag"/> calls can scan it. Compacts the buffer first
    /// (drops consumed prefix) so retained bytes are bounded by the partial
    /// tag in flight. Throws if appending would exceed <see cref="MaxBufferSize"/>
    /// — under correct input this should never happen.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        Compact();

        var unread = _filled - _consumed;
        var required = unread + chunk.Length;
        if (required > MaxBufferSize)
        {
            throw new InvalidOperationException(
                $"IncrementalHtmlTokenizer buffer would exceed {MaxBufferSize} bytes " +
                $"(unread={unread}, chunk={chunk.Length}). Under the sliding-window " +
                $"contract this means either a single tag exceeds {MaxBufferSize} bytes " +
                $"or a script/style body has no closing marker in that window. " +
                $"Bail the scan — the input is pathological.");
        }
        if (required > _buffer.Length)
        {
            // Grow only as far as needed (still capped by MaxBufferSize above).
            var newSize = Math.Max(_buffer.Length * 2, required);
            if (newSize > MaxBufferSize) newSize = MaxBufferSize;
            var grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _filled);
            _buffer = grown;
        }

        chunk.CopyTo(_buffer.AsSpan(_filled));
        _filled += chunk.Length;
        TrackPeak();
    }

    /// <summary>
    /// Drain the next completed tag from the internal buffer. Returns false
    /// when only a partial tag remains (caller should <see cref="Feed"/> the
    /// next chunk and retry). On a successful emit, the consumed bytes are
    /// dropped IMMEDIATELY (compact-on-emit) so the buffer never carries
    /// already-emitted history between calls.
    /// </summary>
    public bool TryReadTag(out TagEvent evt)
    {
        // Resume any pending script/style body skip from a previous chunk.
        if (_pendingSkipTarget is not null)
        {
            var bufSpan = _buffer.AsSpan(_consumed, _filled - _consumed);
            var hitIdx = bufSpan.IndexOf(_pendingSkipTarget);
            if (hitIdx < 0)
            {
                // Skip most of the buffer but preserve the trailing bytes that
                // could be a partial close-tag match — otherwise a close tag
                // straddling the chunk boundary is missed entirely.
                var preserve = Math.Min(_pendingSkipTarget.Length - 1, _filled - _consumed);
                _consumed = _filled - preserve;
                Compact();
                evt = default;
                return false;
            }
            _consumed += hitIdx;
            _pendingSkipTarget = null;
        }

        while (_consumed < _filled)
        {
            var remaining = _buffer.AsSpan(_consumed, _filled - _consumed);
            var ltIdx = remaining.IndexOf((byte)'<');
            if (ltIdx < 0)
            {
                // No '<' in the remaining buffer; advance past it and wait for more.
                _consumed = _filled;
                Compact();
                evt = default;
                return false;
            }

            var afterLt = _consumed + ltIdx + 1;
            if (afterLt >= _filled)
            {
                // Partial '<' at the end — drop everything before it, wait for chunk.
                _consumed += ltIdx;
                Compact();
                evt = default;
                return false;
            }

            if (IsCommentStart(afterLt))
            {
                // Skip the comment in-place if its end fits in the buffer; otherwise
                // bail with partial state so the next Feed brings the closing '-->'.
                var commentStart = afterLt + 3;
                if (commentStart >= _filled)
                {
                    _consumed += ltIdx;
                    Compact();
                    evt = default;
                    return false;
                }
                var commentBody = _buffer.AsSpan(commentStart, _filled - commentStart);
                var endIdx = commentBody.IndexOf("-->"u8);
                if (endIdx < 0)
                {
                    // Partial comment; preserve the '<' so we re-evaluate after Feed.
                    _consumed += ltIdx;
                    Compact();
                    evt = default;
                    return false;
                }
                _consumed = commentStart + endIdx + 3;
                continue;
            }

            var isClose = _buffer[afterLt] == (byte)'/';
            var nameStart = isClose ? afterLt + 1 : afterLt;
            if (nameStart >= _filled)
            {
                _consumed += ltIdx;
                Compact();
                evt = default;
                return false;
            }

            var tagContent = _buffer.AsSpan(nameStart, _filled - nameStart);
            var gtIdx = tagContent.IndexOf((byte)'>');
            if (gtIdx < 0)
            {
                // Tag isn't closed yet — drop bytes before the '<', wait for next Feed.
                _consumed += ltIdx;
                Compact();
                evt = default;
                return false;
            }

            var inner = tagContent.Slice(0, gtIdx);

            int nameLen = 0;
            while (nameLen < inner.Length)
            {
                var ch = inner[nameLen];
                if (ch == (byte)' ' || ch == (byte)'\t' || ch == (byte)'\n'
                    || ch == (byte)'\r' || ch == (byte)'/') break;
                nameLen++;
            }

            var nameHash = XxHash3.HashToUInt64(inner.Slice(0, nameLen));
            var classHash = isClose ? 0UL : ExtractClassHash(inner.Slice(nameLen));
            var tagStart = _consumed + ltIdx;
            var tagEnd = nameStart + gtIdx + 1;
            evt = new TagEvent(nameHash, classHash, ByteLength: tagEnd - tagStart, IsClose: isClose);
            _consumed = tagEnd;

            if (!isClose)
            {
                if (nameHash == s_scriptHash)
                    PrimeBodySkip("</script>"u8);
                else if (nameHash == s_styleHash)
                    PrimeBodySkip("</style>"u8);
            }
            // Compact-on-emit: drop the bytes we just consumed so the buffer
            // post-emit holds only the partial-tag tail (or zero bytes when
            // no partial tag is in flight).
            Compact();
            return true;
        }

        evt = default;
        return false;
    }

    private void PrimeBodySkip(ReadOnlySpan<byte> closeTag)
    {
        // Try to skip in the buffered window first; if the close tag isn't here,
        // preserve the trailing (closeTag.Length - 1) bytes so a partial match
        // straddling the chunk boundary survives the compaction and can be
        // completed when the next chunk arrives.
        var remaining = _buffer.AsSpan(_consumed, _filled - _consumed);
        var idx = remaining.IndexOf(closeTag);
        if (idx < 0)
        {
            var preserve = Math.Min(closeTag.Length - 1, _filled - _consumed);
            _consumed = _filled - preserve;
            _pendingSkipTarget = closeTag.ToArray();
        }
        else
        {
            _consumed += idx;
        }
    }

    private bool IsCommentStart(int afterLt) =>
        afterLt + 2 < _filled
        && _buffer[afterLt] == (byte)'!'
        && _buffer[afterLt + 1] == (byte)'-'
        && _buffer[afterLt + 2] == (byte)'-';

    private void Compact()
    {
        if (_consumed == 0)
        {
            TrackPeak();
            return;
        }
        var unread = _filled - _consumed;
        if (unread > 0)
            Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, unread);
        _droppedBytes += _consumed;
        _filled = unread;
        _consumed = 0;
        TrackPeak();
    }

    private void TrackPeak()
    {
        var inflight = _filled - _consumed;
        if (inflight > _peakBufferedBytes) _peakBufferedBytes = inflight;
    }

    private static ulong ExtractClassHash(ReadOnlySpan<byte> attrs)
    {
        int i = 0;
        while (i < attrs.Length)
        {
            var slice = attrs.Slice(i);
            var idx = slice.IndexOf("class="u8);
            if (idx < 0) return 0;
            int abs = i + idx;
            if (IsAttrBoundary(attrs, abs))
            {
                int valStart = abs + 6;
                if (valStart >= attrs.Length) return 0;
                var quote = attrs[valStart];
                if (quote != (byte)'"' && quote != (byte)'\'') return 0;
                valStart++;
                var rest = attrs.Slice(valStart);
                int valEnd = rest.IndexOf(quote);
                if (valEnd < 0) return 0;
                return XxHash3.HashToUInt64(rest.Slice(0, valEnd));
            }
            i = abs + 6;
        }
        return 0;
    }

    private static bool IsAttrBoundary(ReadOnlySpan<byte> attrs, int pos)
    {
        if (pos == 0) return true;
        var prev = attrs[pos - 1];
        return prev == (byte)' ' || prev == (byte)'\t' || prev == (byte)'\n' || prev == (byte)'\r';
    }
}
