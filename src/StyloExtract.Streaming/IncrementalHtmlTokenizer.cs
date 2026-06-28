using System.IO.Hashing;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Stateful HTML tokenizer that survives chunk boundaries with a TRUE
/// sliding-window byte buffer: only the bytes of the partial tag currently
/// straddling a chunk boundary are retained. Each chunk fed via
/// <see cref="Feed"/> is parsed inline; bytes of complete tags are
/// dropped immediately and only the partial-tag tail (typically &lt;500 B)
/// is copied into <c>_buffer</c> for stitching with the next chunk.
///
/// This is the alpha.21 redesign of the alpha.19 tokenizer, which copied
/// the WHOLE chunk into <c>_buffer</c> on every Feed (so
/// <see cref="PeakBufferedBytes"/> measured chunk-size, not partial-tag
/// size). The new contract: the worst-case retained buffer is the longest
/// single tag that happened to straddle a chunk boundary, regardless of
/// chunk size — so <see cref="PeakBufferedBytes"/> on a 4 KiB- or 16 KiB-
/// chunked feed is the same low-hundreds-of-bytes number.
///
/// Behavioural contract (unchanged): feeding the same bytes that
/// <see cref="MinimalHtmlTokenizer"/> would consume — in any chunking —
/// yields the same <see cref="TagEvent"/> sequence in the same order,
/// with the same hashes and byte lengths.
///
/// Buffer growth: the partial-tag buffer is hard-capped at
/// <see cref="MaxBufferSize"/> (4 KiB). The cap is a safety stop; if a
/// single tag is genuinely longer than 4 KiB (or a script/style body has
/// no close-marker in 4 KiB of body), <see cref="Feed"/> throws
/// <see cref="InvalidOperationException"/>. <see cref="PeakBufferedBytes"/>
/// exposes the high-watermark for memory telemetry.
/// </summary>
public sealed class IncrementalHtmlTokenizer
{
    /// <summary>
    /// Hard safety cap on the internal partial-tag buffer. Under correct input
    /// the buffer's valid-byte count post-Feed is O(longest partial tag).
    /// The cap fires only on input where a single tag exceeds the cap, or a
    /// script/style body has no closing marker inside the cap's worth of body.
    ///
    /// Bumped from 4 KiB to 16 KiB after BBC News + Guardian dogfood crashes:
    /// modern news sites legitimately ship single tags (JSON-LD blobs in
    /// <c>&lt;script type="application/ld+json"&gt;</c>, OpenGraph
    /// <c>&lt;meta&gt;</c> tags with long content attributes, inline SVG with
    /// embedded path strings) in the 4-12 KiB range. 16 KiB covers real-world
    /// maxes while still acting as a safety stop for genuinely broken input.
    /// </summary>
    public const int MaxBufferSize = 16 * 1024;

    private const int InitialBufferSize = 512;

    private static readonly ulong s_scriptHash = XxHash3.HashToUInt64("script"u8);
    private static readonly ulong s_styleHash = XxHash3.HashToUInt64("style"u8);
    private static readonly byte[] s_scriptClose = "</script>"u8.ToArray();
    private static readonly byte[] s_styleClose = "</style>"u8.ToArray();

    private byte[] _buffer;
    private int _bufferLen;       // valid bytes in _buffer
    private long _bytesEmitted;   // bytes that have been parsed into TagEvents (plus inter-tag text)
    private int _peakBufferedBytes;

    // Skip-state for script/style bodies that straddle chunks. When non-null,
    // the next Feed must scan for this close-tag before resuming normal parse.
    private byte[]? _pendingSkipTarget;

    // Events parsed during the most recent Feed but not yet drained by TryReadTag.
    private readonly Queue<TagEvent> _pendingEvents = new();

    // Tag-hash prefilter for the per-tag attribute pass; see MinimalHtmlTokenizer.
    private readonly TripwireTagFilter _filter;

    public IncrementalHtmlTokenizer()
        : this(TripwireTagFilter.MatchAll)
    {
    }

    public IncrementalHtmlTokenizer(TripwireTagFilter filter)
    {
        _buffer = new byte[InitialBufferSize];
        _filter = filter;
    }

    public long BytesConsumed => _bytesEmitted;
    public int PeakBufferedBytes => _peakBufferedBytes;

    /// <summary>
    /// Feed the next chunk of bytes. Parses everything into <see cref="TagEvent"/>s
    /// inline; only the partial-tag tail (if a tag straddles the end of the
    /// chunk) is retained for the next call. Throws if the retained tail
    /// would exceed <see cref="MaxBufferSize"/>.
    /// </summary>
    public void Feed(ReadOnlySpan<byte> chunk)
    {
        if (chunk.IsEmpty)
        {
            TrackPeak();
            return;
        }

        // Strategy: maintain a logical input position. If _buffer is non-empty,
        // we must stitch it with chunk; we do this by appending small slices
        // of chunk into _buffer (one byte at a time) until either a tag or
        // comment completes in _buffer, OR _buffer goes empty (text-only
        // prefix drained), at which point we switch to scanning chunk's
        // remainder DIRECTLY (no copy). Once the _buffer is empty, we never
        // copy bytes from chunk into _buffer until we hit a partial-tag tail
        // at chunk-end.

        int chunkPos = 0;

        // Phase 0: resume pending script/style body skip.
        if (_pendingSkipTarget is not null)
        {
            ResumeBodySkipStitched(chunk, ref chunkPos);
            if (_pendingSkipTarget is not null)
            {
                // Still skipping at chunk-end.
                PreserveBodyCloseTail(chunk, chunkPos);
                TrackPeak();
                return;
            }
            // Skip completed; _buffer may have residual bytes (close-tag onward).
        }

        // Phase 1: drain _buffer (the partial tag/comment from previous Feed)
        // by appending chunk bytes one at a time until the buffer is empty
        // OR we run out of chunk OR a script/style skip is primed.
        while (_bufferLen > 0 && chunkPos < chunk.Length)
        {
            // Try parsing what's already in _buffer first; we might already
            // have enough.
            if (TryParseStepFromBuffer())
            {
                if (_pendingSkipTarget is not null)
                {
                    // The parsed tag opened a script/style body. Continue the
                    // skip across _buffer + chunk.
                    ResumeBodySkipStitched(chunk, ref chunkPos);
                    if (_pendingSkipTarget is not null)
                    {
                        PreserveBodyCloseTail(chunk, chunkPos);
                        TrackPeak();
                        return;
                    }
                }
                continue;
            }
            // _buffer is incomplete; append one byte from chunk and retry.
            EnsureBufferCapacity(_bufferLen + 1);
            _buffer[_bufferLen++] = chunk[chunkPos++];
        }

        // After Phase 1 either _buffer is empty (we drained it into events
        // and chunk has more bytes) or chunk is exhausted while _buffer still
        // holds a partial tag.

        if (chunkPos >= chunk.Length)
        {
            // Chunk exhausted. Whatever's in _buffer carries forward.
            // But: if _buffer holds pure-text-then-something, drain text now.
            DrainBufferTextPrefix();
            // Try one more parse step in case the very last appended byte
            // completed a tag boundary check; ignore result.
            while (TryParseStepFromBuffer())
            {
                if (_pendingSkipTarget is not null)
                {
                    // Body opened with no further chunk bytes; preserve
                    // remaining _buffer as body bytes via the skip path.
                    ResumeBodySkipStitched(ReadOnlySpan<byte>.Empty, ref chunkPos);
                    if (_pendingSkipTarget is not null)
                    {
                        // Close not found; preserve close-marker straddle bytes
                        // (in this case _buffer already holds the body bytes).
                        // Keep only the tail bytes useful for stitching.
                        TrimBufferToCloseMarkerTail();
                        TrackPeak();
                        return;
                    }
                }
            }
            TrackPeak();
            return;
        }

        // Phase 2: _buffer is empty. Scan chunk directly until either we
        // emit all complete tags or we hit a partial tag at chunk-end.
        ScanChunkInPlace(chunk, ref chunkPos);

        // Phase 3: if the scan stopped mid-tag, copy the tail into _buffer.
        // ScanChunkInPlace leaves chunkPos pointing at the start of the
        // partial tag (the '<'), so the remaining bytes are the partial tag.
        if (_pendingSkipTarget is null && chunkPos < chunk.Length)
        {
            var tail = chunk.Length - chunkPos;
            EnsureBufferCapacity(tail);
            chunk.Slice(chunkPos, tail).CopyTo(_buffer.AsSpan());
            _bufferLen = tail;
        }
        TrackPeak();
    }

    public bool TryReadTag(out TagEvent evt)
    {
        if (_pendingEvents.Count == 0)
        {
            evt = default;
            return false;
        }
        evt = _pendingEvents.Dequeue();
        return true;
    }

    /// <summary>
    /// Try to parse one step (a tag emit, a comment skip, or a text-prefix
    /// drain) from the current <c>_buffer</c>. Returns true if any progress
    /// was made (bytes consumed from buffer, possibly an event emitted).
    /// Returns false if buffer holds an incomplete tag/comment awaiting
    /// more bytes.
    /// </summary>
    private bool TryParseStepFromBuffer()
    {
        if (_bufferLen == 0) return false;

        var buf = _buffer.AsSpan(0, _bufferLen);
        var ltIdx = buf.IndexOf((byte)'<');
        if (ltIdx < 0)
        {
            // Pure text in _buffer; drop it and count toward emitted bytes.
            _bytesEmitted += _bufferLen;
            _bufferLen = 0;
            return true;
        }

        var afterLt = ltIdx + 1;
        if (afterLt >= _bufferLen) return false; // '<' at end

        // Comment?
        if (IsCommentStartInBuffer(afterLt))
        {
            var commentStart = afterLt + 3;
            if (commentStart >= _bufferLen) return false;
            var commentBody = buf.Slice(commentStart);
            var endIdx = commentBody.IndexOf("-->"u8);
            if (endIdx < 0) return false;
            var commentTotal = commentStart + endIdx + 3; // from start of _buffer
            _bytesEmitted += commentTotal;
            CompactBufferFrom(commentTotal);
            return true;
        }

        var isClose = _buffer[afterLt] == (byte)'/';
        var nameStart = isClose ? afterLt + 1 : afterLt;
        if (nameStart >= _bufferLen) return false;

        var tagContent = buf.Slice(nameStart);
        var gtIdx = tagContent.IndexOf((byte)'>');
        if (gtIdx < 0) return false;

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
        var attrs = isClose ? ReadOnlySpan<byte>.Empty : inner.Slice(nameLen);
        var tagEnd = nameStart + gtIdx + 1;
        var tagByteLen = tagEnd - ltIdx;

        var isInteresting = !isClose && _filter.Matches(nameHash);
        var classHash = isInteresting ? TagAttributeParser.ExtractClassHash(attrs) : 0UL;

        ulong idHash = 0UL;
        ulong roleHash = 0UL;
        ulong[] classHashes = Array.Empty<ulong>();
        AttrHashPair[] dataAttrs = Array.Empty<AttrHashPair>();
        AttrHashPair[] ariaAttrs = Array.Empty<AttrHashPair>();
        if (isInteresting && !attrs.IsEmpty)
        {
            TagAttributeParser.ExtractIdentityHashes(
                attrs, out idHash, out roleHash, out classHashes, out dataAttrs, out ariaAttrs);
        }

        _pendingEvents.Enqueue(new TagEvent
        {
            TagNameHash = nameHash,
            ClassHash = classHash,
            IdHash = idHash,
            RoleHash = roleHash,
            ClassHashes = classHashes,
            DataAttrHashes = dataAttrs,
            AriaAttrHashes = ariaAttrs,
            ByteLength = tagByteLen,
            IsClose = isClose,
        });
        _bytesEmitted += ltIdx + tagByteLen;
        CompactBufferFrom(tagEnd);

        if (!isClose)
        {
            if (nameHash == s_scriptHash) _pendingSkipTarget = s_scriptClose;
            else if (nameHash == s_styleHash) _pendingSkipTarget = s_styleClose;
        }
        return true;
    }

    private void DrainBufferTextPrefix()
    {
        if (_bufferLen == 0) return;
        var buf = _buffer.AsSpan(0, _bufferLen);
        var ltIdx = buf.IndexOf((byte)'<');
        if (ltIdx < 0)
        {
            _bytesEmitted += _bufferLen;
            _bufferLen = 0;
            return;
        }
        if (ltIdx > 0)
        {
            _bytesEmitted += ltIdx;
            CompactBufferFrom(ltIdx);
        }
    }

    private void CompactBufferFrom(int from)
    {
        var residual = _bufferLen - from;
        if (residual > 0)
            Buffer.BlockCopy(_buffer, from, _buffer, 0, residual);
        _bufferLen = residual;
    }

    /// <summary>
    /// Scan tags directly from <paramref name="chunk"/> starting at
    /// <paramref name="chunkPos"/>. Stops when (a) a tag straddles
    /// chunk-end, (b) a comment straddles chunk-end, (c) script/style
    /// body skipping starts and its close marker isn't in the remaining
    /// chunk. On stop, <paramref name="chunkPos"/> points at the start
    /// of the unparsed remainder (the '&lt;' for partial tags, or beyond
    /// the partial close-marker tail for body skips).
    /// </summary>
    private void ScanChunkInPlace(ReadOnlySpan<byte> chunk, ref int chunkPos)
    {
        while (chunkPos < chunk.Length)
        {
            var remaining = chunk.Slice(chunkPos);
            var ltIdx = remaining.IndexOf((byte)'<');
            if (ltIdx < 0)
            {
                _bytesEmitted += remaining.Length;
                chunkPos = chunk.Length;
                return;
            }

            var afterLt = chunkPos + ltIdx + 1;
            if (afterLt >= chunk.Length)
            {
                // '<' at chunk-end; preserve from '<' onward.
                _bytesEmitted += ltIdx;
                chunkPos += ltIdx;
                return;
            }

            if (IsCommentStartInChunk(chunk, afterLt))
            {
                var commentStart = afterLt + 3;
                if (commentStart >= chunk.Length)
                {
                    _bytesEmitted += ltIdx;
                    chunkPos += ltIdx;
                    return;
                }
                var commentBody = chunk.Slice(commentStart);
                var endIdx = commentBody.IndexOf("-->"u8);
                if (endIdx < 0)
                {
                    _bytesEmitted += ltIdx;
                    chunkPos += ltIdx;
                    return;
                }
                var commentTotal = (commentStart - chunkPos) + endIdx + 3;
                _bytesEmitted += commentTotal;
                chunkPos += commentTotal;
                continue;
            }

            var isClose = chunk[afterLt] == (byte)'/';
            var nameStart = isClose ? afterLt + 1 : afterLt;
            if (nameStart >= chunk.Length)
            {
                _bytesEmitted += ltIdx;
                chunkPos += ltIdx;
                return;
            }

            var tagContent = chunk.Slice(nameStart);
            var gtIdx = tagContent.IndexOf((byte)'>');
            if (gtIdx < 0)
            {
                _bytesEmitted += ltIdx;
                chunkPos += ltIdx;
                return;
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
            var attrs = isClose ? ReadOnlySpan<byte>.Empty : inner.Slice(nameLen);
            var tagStart = chunkPos + ltIdx;
            var tagEnd = nameStart + gtIdx + 1;
            var tagByteLen = tagEnd - tagStart;

            var isInteresting = !isClose && _filter.Matches(nameHash);
            var classHash = isInteresting ? TagAttributeParser.ExtractClassHash(attrs) : 0UL;

            ulong idHash = 0UL;
            ulong roleHash = 0UL;
            ulong[] classHashes = Array.Empty<ulong>();
            AttrHashPair[] dataAttrs = Array.Empty<AttrHashPair>();
            AttrHashPair[] ariaAttrs = Array.Empty<AttrHashPair>();
            if (isInteresting && !attrs.IsEmpty)
            {
                TagAttributeParser.ExtractIdentityHashes(
                    attrs, out idHash, out roleHash, out classHashes, out dataAttrs, out ariaAttrs);
            }

            _pendingEvents.Enqueue(new TagEvent
            {
                TagNameHash = nameHash,
                ClassHash = classHash,
                IdHash = idHash,
                RoleHash = roleHash,
                ClassHashes = classHashes,
                DataAttrHashes = dataAttrs,
                AriaAttrHashes = ariaAttrs,
                ByteLength = tagByteLen,
                IsClose = isClose,
            });
            _bytesEmitted += ltIdx + tagByteLen;
            chunkPos = tagEnd;

            if (!isClose)
            {
                if (nameHash == s_scriptHash) _pendingSkipTarget = s_scriptClose;
                else if (nameHash == s_styleHash) _pendingSkipTarget = s_styleClose;

                if (_pendingSkipTarget is not null)
                {
                    // Inline body skip in chunk.
                    var body = chunk.Slice(chunkPos);
                    var closeIdx = body.IndexOf(_pendingSkipTarget);
                    if (closeIdx >= 0)
                    {
                        _bytesEmitted += closeIdx;
                        chunkPos += closeIdx;
                        _pendingSkipTarget = null;
                    }
                    else
                    {
                        // Body straddles chunk-end. Consume body bytes up to
                        // (length - (closeTag.Length - 1)); preserve the rest
                        // for stitching.
                        var preserve = Math.Min(_pendingSkipTarget.Length - 1, body.Length);
                        var consumed = body.Length - preserve;
                        _bytesEmitted += consumed;
                        chunkPos = chunk.Length - preserve;
                        // Don't return yet; let Phase 3 copy the tail into _buffer.
                        return;
                    }
                }
            }
        }
    }

    /// <summary>
    /// While skipping a body, search for the close marker across
    /// (<c>_buffer</c> + <paramref name="chunk"/>). On success, consume up
    /// to the close marker (leave the marker as the next thing to parse)
    /// and clear <c>_pendingSkipTarget</c>. On failure, consume all but
    /// (markerLen - 1) trailing bytes — those are stored as a straddle
    /// fragment by the caller.
    /// </summary>
    private void ResumeBodySkipStitched(ReadOnlySpan<byte> chunk, ref int chunkPos)
    {
        if (_pendingSkipTarget is null) return;
        var target = _pendingSkipTarget;

        // First search within _buffer alone.
        if (_bufferLen > 0)
        {
            var buf = _buffer.AsSpan(0, _bufferLen);
            var idx = buf.IndexOf(target);
            if (idx >= 0)
            {
                _bytesEmitted += idx;
                CompactBufferFrom(idx);
                _pendingSkipTarget = null;
                return;
            }
            // Stitched window: last (target.Length - 1) bytes of _buffer
            // concatenated with first (target.Length - 1) bytes of chunk.
            // If the marker straddles, it'll appear here.
            var bufTail = Math.Min(target.Length - 1, _bufferLen);
            var chunkHead = Math.Min(target.Length - 1, chunk.Length - chunkPos);
            if (bufTail > 0 && chunkHead > 0)
            {
                Span<byte> stitch = stackalloc byte[bufTail + chunkHead];
                _buffer.AsSpan(_bufferLen - bufTail, bufTail).CopyTo(stitch);
                chunk.Slice(chunkPos, chunkHead).CopyTo(stitch.Slice(bufTail));
                var sIdx = stitch.IndexOf(target);
                if (sIdx >= 0)
                {
                    if (sIdx < bufTail)
                    {
                        // Marker starts inside _buffer's tail.
                        var bufConsumed = (_bufferLen - bufTail) + sIdx;
                        _bytesEmitted += bufConsumed;
                        CompactBufferFrom(bufConsumed);
                        _pendingSkipTarget = null;
                        return;
                    }
                    else
                    {
                        // Marker starts inside chunk. Drop _buffer entirely;
                        // advance chunk to marker.
                        var advChunk = sIdx - bufTail;
                        _bytesEmitted += _bufferLen + advChunk;
                        _bufferLen = 0;
                        chunkPos += advChunk;
                        _pendingSkipTarget = null;
                        return;
                    }
                }
            }
            // Marker doesn't straddle. Drop _buffer entirely (it's all body).
            _bytesEmitted += _bufferLen;
            _bufferLen = 0;
        }

        // Now search chunk alone.
        var body = chunk.Slice(chunkPos);
        var bodyIdx = body.IndexOf(target);
        if (bodyIdx >= 0)
        {
            _bytesEmitted += bodyIdx;
            chunkPos += bodyIdx;
            _pendingSkipTarget = null;
            return;
        }
        // Consume up to (length - (target.Length - 1)).
        var preserve = Math.Min(target.Length - 1, body.Length);
        var consumed = body.Length - preserve;
        _bytesEmitted += consumed;
        chunkPos = chunk.Length - preserve;
    }

    /// <summary>
    /// At the end of Feed, when still in body-skip mode, preserve the
    /// trailing (target.Length - 1) bytes of <paramref name="chunk"/> in
    /// <c>_buffer</c> as a close-marker straddle fragment.
    /// </summary>
    private void PreserveBodyCloseTail(ReadOnlySpan<byte> chunk, int chunkPos)
    {
        var target = _pendingSkipTarget!;
        var available = chunk.Length - chunkPos;
        var preserve = Math.Min(target.Length - 1, available);
        if (preserve <= 0)
        {
            _bufferLen = 0;
            return;
        }
        EnsureBufferCapacity(preserve);
        chunk.Slice(chunk.Length - preserve, preserve).CopyTo(_buffer.AsSpan());
        _bufferLen = preserve;
    }

    /// <summary>
    /// When in body-skip mode at end-of-feed with no further chunk, trim
    /// <c>_buffer</c> down to its trailing (target.Length - 1) bytes so we
    /// preserve only the potential close-marker fragment.
    /// </summary>
    private void TrimBufferToCloseMarkerTail()
    {
        var target = _pendingSkipTarget!;
        var preserve = Math.Min(target.Length - 1, _bufferLen);
        if (preserve < _bufferLen)
        {
            var drop = _bufferLen - preserve;
            _bytesEmitted += drop;
            Buffer.BlockCopy(_buffer, drop, _buffer, 0, preserve);
            _bufferLen = preserve;
        }
    }

    private bool IsCommentStartInBuffer(int afterLt) =>
        afterLt + 2 < _bufferLen
        && _buffer[afterLt] == (byte)'!'
        && _buffer[afterLt + 1] == (byte)'-'
        && _buffer[afterLt + 2] == (byte)'-';

    private static bool IsCommentStartInChunk(ReadOnlySpan<byte> chunk, int afterLt) =>
        afterLt + 2 < chunk.Length
        && chunk[afterLt] == (byte)'!'
        && chunk[afterLt + 1] == (byte)'-'
        && chunk[afterLt + 2] == (byte)'-';

    private void EnsureBufferCapacity(int required)
    {
        if (required > MaxBufferSize)
        {
            throw new InvalidOperationException(
                $"IncrementalHtmlTokenizer partial-tag buffer would exceed {MaxBufferSize} bytes " +
                $"(required={required}). Under the sliding-window contract this means either a " +
                $"single tag exceeds {MaxBufferSize} bytes or a script/style body has no closing " +
                $"marker in that window. Bail the scan — the input is pathological.");
        }
        if (required > _buffer.Length)
        {
            var newSize = Math.Max(_buffer.Length * 2, required);
            if (newSize > MaxBufferSize) newSize = MaxBufferSize;
            var grown = new byte[newSize];
            Buffer.BlockCopy(_buffer, 0, grown, 0, _bufferLen);
            _buffer = grown;
        }
    }

    private void TrackPeak()
    {
        if (_bufferLen > _peakBufferedBytes) _peakBufferedBytes = _bufferLen;
    }

}
