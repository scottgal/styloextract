using System.IO.Hashing;

namespace StyloExtract.Streaming;

/// <summary>
/// Stateful HTML tokenizer that survives chunk boundaries. Append bytes via
/// <see cref="Feed"/> as they arrive from the network; drain completed tag
/// events via <see cref="TryReadTag"/>. A partial tag at the end of one chunk
/// (e.g. <c>"&lt;hea"</c>) is held in an internal buffer and completed when the
/// next chunk arrives.
///
/// Heap-allocated (one pinned-ish buffer) — not zero-alloc like
/// <see cref="MinimalHtmlTokenizer"/>'s span path. Use this for streaming-gateway
/// scenarios where bytes arrive in chunks and you can't wait for the whole
/// response. Trade-off vs the span tokenizer: one buffer allocation per request
/// (the internal byte array, geometrically grown), not per chunk.
///
/// Behavioural contract: feeding the same bytes that <see cref="MinimalHtmlTokenizer"/>
/// would consume — in any chunking — yields the same <see cref="TagEvent"/>
/// sequence in the same order, with the same hashes and byte lengths. The
/// stateful path is structurally equivalent to the span path, just resumable.
///
/// Buffer growth: starts at 8 KiB, doubles up to <see cref="MaxBufferSize"/> on
/// each <see cref="Feed"/>. When the limit is reached and no consumable bytes
/// have made progress, <see cref="Feed"/> throws <see cref="InvalidOperationException"/>
/// rather than silently dropping bytes — this surfaces pathological inputs
/// (no tags ever close) loudly enough to bail at the call site.
/// </summary>
public sealed class IncrementalHtmlTokenizer
{
    /// <summary>Hard cap on the internal byte buffer to prevent OOM on pathological inputs.</summary>
    public const int MaxBufferSize = 1 * 1024 * 1024; // 1 MiB

    private const int InitialBufferSize = 8 * 1024;

    private static readonly ulong s_scriptHash = XxHash3.HashToUInt64("script"u8);
    private static readonly ulong s_styleHash = XxHash3.HashToUInt64("style"u8);

    private byte[] _buffer;
    private int _filled;     // index past the last byte fed into _buffer
    private int _consumed;   // index of the next byte the tokenizer will look at
    private long _droppedBytes; // bytes dropped during compaction (kept so BytesConsumed monotonically grows)

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
    /// Append <paramref name="chunk"/> to the internal buffer so subsequent
    /// <see cref="TryReadTag"/> calls can scan it. Compacts the buffer first
    /// (drops consumed prefix); grows the buffer geometrically if needed.
    /// Throws if appending would exceed <see cref="MaxBufferSize"/> with no
    /// progress possible.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty) return;

        Compact();

        var unread = _filled - _consumed;
        var required = unread + chunk.Length;
        if (required > _buffer.Length)
        {
            var newSize = _buffer.Length;
            while (newSize < required) newSize = checked(newSize * 2);
            if (newSize > MaxBufferSize)
            {
                if (required > MaxBufferSize)
                {
                    throw new InvalidOperationException(
                        $"IncrementalHtmlTokenizer buffer would exceed {MaxBufferSize} bytes " +
                        $"(unread={unread}, chunk={chunk.Length}). Input has no closing tags " +
                        $"in the buffered window — bail the scan.");
                }
                newSize = MaxBufferSize;
            }
            var grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, _consumed, grown, 0, unread);
            _droppedBytes += _consumed;
            _consumed = 0;
            _filled = unread;
            _buffer = grown;
        }

        chunk.CopyTo(_buffer.AsSpan(_filled));
        _filled += chunk.Length;
    }

    /// <summary>
    /// Drain the next completed tag from the internal buffer. Returns false
    /// when only a partial tag remains (caller should <see cref="Feed"/> the
    /// next chunk and retry).
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
                evt = default;
                return false;
            }

            var afterLt = _consumed + ltIdx + 1;
            if (afterLt >= _filled)
            {
                // Partial '<' at the end — wait for the next chunk to bring the tag name.
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
                    evt = default;
                    return false;
                }
                var commentBody = _buffer.AsSpan(commentStart, _filled - commentStart);
                var endIdx = commentBody.IndexOf("-->"u8);
                if (endIdx < 0)
                {
                    // Partial comment; preserve the '<' so we re-evaluate after Feed.
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
                evt = default;
                return false;
            }

            var tagContent = _buffer.AsSpan(nameStart, _filled - nameStart);
            var gtIdx = tagContent.IndexOf((byte)'>');
            if (gtIdx < 0)
            {
                // Tag isn't closed yet — wait for next Feed.
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
        if (_consumed == 0) return;
        var unread = _filled - _consumed;
        if (unread > 0)
            Buffer.BlockCopy(_buffer, _consumed, _buffer, 0, unread);
        _droppedBytes += _consumed;
        _filled = unread;
        _consumed = 0;
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
