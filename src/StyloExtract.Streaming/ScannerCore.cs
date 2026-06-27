using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Byte-pattern FSM shared between <see cref="BytePatternScanner"/> (ref
/// struct, single-pass) and <see cref="IncrementalBytePatternScanner"/>
/// (heap-backed, chunked). Stateless / pure-static; all per-scan state lives
/// in the caller-owned <see cref="State"/>.
///
/// States: <see cref="FenceState.AwaitPrefix"/> →
/// <see cref="FenceState.AwaitContentStart"/> →
/// <see cref="FenceState.Capturing"/> →
/// <see cref="FenceState.Captured"/> | <see cref="FenceState.Bailed"/>.
///
/// On every byte scan the matcher skips
/// <c>&lt;!-- ... --&gt;</c> comments, <c>&lt;script&gt; ... &lt;/script&gt;</c>
/// and <c>&lt;style&gt; ... &lt;/style&gt;</c> bodies, and
/// <c>&lt;![CDATA[ ... ]]&gt;</c> sections — these regions can carry text
/// that looks like an HTML tag.
///
/// In <see cref="FenceState.Capturing"/> the FSM additionally counts
/// occurrences of the content-start tag name (open vs. close) so an inline
/// <c>&lt;article&gt;example&lt;/article&gt;</c> inside the captured region
/// doesn't close it prematurely. The counter is one int; that's the only
/// concession back to structure-awareness from a pure byte matcher.
/// </summary>
internal static class ScannerCore
{
    /// <summary>
    /// All per-scan state lives here so both scanner shells can share the
    /// FSM.
    /// </summary>
    internal struct State
    {
        public FenceState Fence;
        public long TotalBytesConsumed;
        public long BytesSinceStateChange;
        public long CaptureStartByte;
        public long CaptureEndByte;
        /// <summary>
        /// Counter for nested opens of the content-start tag name while in
        /// <see cref="FenceState.Capturing"/>. Set to 1 on entry; each
        /// matching open increments; each matching close decrements. The
        /// content-end pattern only fires when the counter would reach 0.
        /// </summary>
        public int OpenCounter;

        public static State Initial => new() { Fence = FenceState.AwaitPrefix };
    }

    /// <summary>
    /// Drive the FSM over <paramref name="input"/>. Returns
    /// <see cref="ScanVerdict.Captured"/> or <see cref="ScanVerdict.Bailout"/>
    /// when terminal; <see cref="ScanVerdict.Continue"/> when more input is
    /// needed. <paramref name="bytesConsumedFromInput"/> reports how many
    /// bytes of <paramref name="input"/> were definitively consumed — the
    /// chunked scanner stitches the remainder with the next chunk.
    /// </summary>
    public static ScanVerdict Step(
        ReadOnlySpan<byte> input,
        ref State state,
        in StreamingTemplate template,
        out int bytesConsumedFromInput,
        bool isFinalChunk = false)
    {
        bytesConsumedFromInput = 0;
        if (state.Fence == FenceState.Captured) return ScanVerdict.Captured;
        if (state.Fence == FenceState.Bailed) return ScanVerdict.Bailout;

        int cursor = 0;
        while (cursor < input.Length)
        {
            if (input[cursor] == (byte)'<')
            {
                // Skip-region check first — script/style bodies may contain
                // text that LOOKS like an HTML tag.
                var skipKind = ClassifySkipRegion(input, cursor);
                if (skipKind != SkipKind.None)
                {
                    var (advanced, complete) = TrySkipRegion(input, cursor, skipKind);
                    if (!complete)
                    {
                        state.TotalBytesConsumed += advanced;
                        state.BytesSinceStateChange += advanced;
                        bytesConsumedFromInput = cursor + advanced;
                        if (CheckBailout(ref state, in template)) return ScanVerdict.Bailout;
                        return ScanVerdict.Continue;
                    }
                    state.TotalBytesConsumed += advanced;
                    state.BytesSinceStateChange += advanced;
                    cursor += advanced;
                    if (CheckBailout(ref state, in template))
                    {
                        bytesConsumedFromInput = cursor;
                        return ScanVerdict.Bailout;
                    }
                    continue;
                }

                int patternLen;
                bool patternNeedsMore;
                switch (state.Fence)
                {
                    case FenceState.AwaitPrefix:
                        patternLen = BytePatternDfa.TryMatchAt(input, cursor, template.PrefixPattern, out patternNeedsMore);
                        break;
                    case FenceState.AwaitContentStart:
                        patternLen = BytePatternDfa.TryMatchAt(input, cursor, template.ContentStartPattern, out patternNeedsMore);
                        break;
                    case FenceState.Capturing:
                        patternLen = BytePatternDfa.TryMatchAt(input, cursor, template.ContentEndPattern, out patternNeedsMore);
                        break;
                    default:
                        patternLen = 0;
                        patternNeedsMore = false;
                        break;
                }

                if (patternLen > 0)
                {
                    switch (state.Fence)
                    {
                        case FenceState.AwaitPrefix:
                            state.Fence = FenceState.AwaitContentStart;
                            state.TotalBytesConsumed += patternLen;
                            cursor += patternLen;
                            state.BytesSinceStateChange = 0;
                            break;
                        case FenceState.AwaitContentStart:
                            state.Fence = FenceState.Capturing;
                            state.CaptureStartByte = state.TotalBytesConsumed;
                            state.OpenCounter = 1;
                            state.TotalBytesConsumed += patternLen;
                            cursor += patternLen;
                            state.BytesSinceStateChange = 0;
                            break;
                        case FenceState.Capturing:
                            if (state.OpenCounter <= 1)
                            {
                                state.TotalBytesConsumed += patternLen;
                                state.CaptureEndByte = state.TotalBytesConsumed;
                                state.Fence = FenceState.Captured;
                                bytesConsumedFromInput = cursor + patternLen;
                                return ScanVerdict.Captured;
                            }
                            // Nested same-name close at deeper level — count it
                            // down and keep scanning.
                            state.OpenCounter--;
                            state.TotalBytesConsumed += patternLen;
                            state.BytesSinceStateChange += patternLen;
                            cursor += patternLen;
                            break;
                    }
                    if (CheckBailout(ref state, in template))
                    {
                        bytesConsumedFromInput = cursor;
                        return ScanVerdict.Bailout;
                    }
                    continue;
                }

                // In Capturing, also watch for nested opens / closes of the
                // content-start tag name itself so the open counter stays
                // accurate.
                if (state.Fence == FenceState.Capturing
                    && TryCountSameTag(input, cursor, in template, out var sameTagLen, out var isClose))
                {
                    if (isClose && state.OpenCounter > 0) state.OpenCounter--;
                    else if (!isClose) state.OpenCounter++;
                    state.TotalBytesConsumed += sameTagLen;
                    state.BytesSinceStateChange += sameTagLen;
                    cursor += sameTagLen;
                    if (CheckBailout(ref state, in template))
                    {
                        bytesConsumedFromInput = cursor;
                        return ScanVerdict.Bailout;
                    }
                    continue;
                }

                // No pattern/skip matched at this '<'. Carry over the '<'
                // onward when (a) the matcher ran out of bytes mid-pattern,
                // or (b) we don't have enough bytes after '<' to definitively
                // classify a skip region (longest opener is "<![CDATA[" = 9
                // bytes). On the final chunk we advance past — no more bytes
                // are coming.
                if (!isFinalChunk
                    && (patternNeedsMore || input.Length - cursor < 9))
                {
                    bytesConsumedFromInput = cursor;
                    if (CheckBailout(ref state, in template)) return ScanVerdict.Bailout;
                    return ScanVerdict.Continue;
                }
            }

            cursor++;
            state.TotalBytesConsumed++;
            state.BytesSinceStateChange++;
            if (CheckBailout(ref state, in template))
            {
                bytesConsumedFromInput = cursor;
                return ScanVerdict.Bailout;
            }
        }

        bytesConsumedFromInput = cursor;
        return state.Fence == FenceState.Captured ? ScanVerdict.Captured
            : state.Fence == FenceState.Bailed ? ScanVerdict.Bailout
            : ScanVerdict.Continue;
    }

    private static bool CheckBailout(ref State state, in StreamingTemplate template)
    {
        if ((state.Fence == FenceState.AwaitPrefix || state.Fence == FenceState.AwaitContentStart)
            && state.BytesSinceStateChange > template.BailoutBytes)
        {
            state.Fence = FenceState.Bailed;
            return true;
        }
        if (state.Fence == FenceState.Capturing
            && (state.TotalBytesConsumed - state.CaptureStartByte) > template.MaxCaptureBytes)
        {
            state.Fence = FenceState.Bailed;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Test whether the byte at <paramref name="cursor"/> opens a tag whose
    /// name equals the content-start pattern's tag name (in either open or
    /// close form). Used inside <see cref="FenceState.Capturing"/> to keep
    /// the nested-open counter accurate when the active end pattern itself
    /// didn't match (different attributes, or this is the start side of a
    /// nested pair).
    /// </summary>
    private static bool TryCountSameTag(
        ReadOnlySpan<byte> input,
        int cursor,
        in StreamingTemplate template,
        out int len,
        out bool isClose)
    {
        len = 0;
        isClose = false;
        if (input[cursor] != (byte)'<') return false;
        if (cursor + 1 >= input.Length) return false;

        isClose = input[cursor + 1] == (byte)'/';
        var name = template.ContentStartPattern.TagNameSpan;
        int nameStart = cursor + (isClose ? 2 : 1);
        if (nameStart + name.Length > input.Length) return false;
        if (!input.Slice(nameStart, name.Length).SequenceEqual(name)) return false;
        var after = nameStart + name.Length;
        if (after >= input.Length) return false;
        var ch = input[after];
        if (!(ch == (byte)' ' || ch == (byte)'\t' || ch == (byte)'\n'
            || ch == (byte)'\r' || ch == (byte)'\f' || ch == (byte)'/' || ch == (byte)'>'))
            return false;

        var rest = input.Slice(after);
        // Walk to '>' tracking quote state so a quoted '>' doesn't fool us.
        byte quote = 0;
        int i = 0;
        while (i < rest.Length)
        {
            var b = rest[i];
            if (quote != 0)
            {
                if (b == quote) quote = 0;
            }
            else if (b == (byte)'"' || b == (byte)'\'') quote = b;
            else if (b == (byte)'>')
            {
                len = (after - cursor) + i + 1;
                return true;
            }
            i++;
        }
        return false;
    }

    private enum SkipKind : byte { None, Comment, Cdata, Script, Style }

    private static SkipKind ClassifySkipRegion(ReadOnlySpan<byte> input, int cursor)
    {
        if (cursor + 3 < input.Length
            && input[cursor + 1] == (byte)'!'
            && input[cursor + 2] == (byte)'-'
            && input[cursor + 3] == (byte)'-')
        {
            return SkipKind.Comment;
        }
        if (cursor + 8 < input.Length
            && input[cursor + 1] == (byte)'!'
            && input[cursor + 2] == (byte)'['
            && input[cursor + 3] == (byte)'C'
            && input[cursor + 4] == (byte)'D'
            && input[cursor + 5] == (byte)'A'
            && input[cursor + 6] == (byte)'T'
            && input[cursor + 7] == (byte)'A'
            && input[cursor + 8] == (byte)'[')
        {
            return SkipKind.Cdata;
        }
        if (StartsWithTagName(input, cursor, "script"u8)) return SkipKind.Script;
        if (StartsWithTagName(input, cursor, "style"u8)) return SkipKind.Style;
        return SkipKind.None;
    }

    private static bool StartsWithTagName(ReadOnlySpan<byte> input, int cursor, ReadOnlySpan<byte> tagName)
    {
        int start = cursor + 1;
        if (start + tagName.Length >= input.Length) return false;
        for (int i = 0; i < tagName.Length; i++)
        {
            var b = input[start + i];
            if (b >= (byte)'A' && b <= (byte)'Z') b += 32;
            if (b != tagName[i]) return false;
        }
        var next = input[start + tagName.Length];
        return next == (byte)' ' || next == (byte)'\t' || next == (byte)'\n'
            || next == (byte)'\r' || next == (byte)'\f' || next == (byte)'/' || next == (byte)'>';
    }

    /// <summary>
    /// Skip past one of the recognised skip regions. Returns bytes advanced
    /// and whether the region completed within <paramref name="input"/>. On
    /// incomplete return, the caller stitches the trailing fragment with the
    /// next chunk before resuming.
    /// </summary>
    private static (int advanced, bool complete) TrySkipRegion(
        ReadOnlySpan<byte> input,
        int cursor,
        SkipKind kind)
    {
        ReadOnlySpan<byte> opener;
        ReadOnlySpan<byte> closer;
        switch (kind)
        {
            case SkipKind.Comment: opener = "<!--"u8; closer = "-->"u8; break;
            case SkipKind.Cdata:   opener = "<![CDATA["u8; closer = "]]>"u8; break;
            case SkipKind.Script:  opener = "<script"u8; closer = "</script>"u8; break;
            case SkipKind.Style:   opener = "<style"u8; closer = "</style>"u8; break;
            default: return (0, false);
        }

        int bodyStart;
        if (kind == SkipKind.Script || kind == SkipKind.Style)
        {
            var after = cursor + opener.Length;
            if (after >= input.Length) return (0, false);
            var rest = input.Slice(after);
            var gt = rest.IndexOf((byte)'>');
            if (gt < 0) return (0, false);
            bodyStart = after + gt + 1;
        }
        else
        {
            bodyStart = cursor + opener.Length;
        }

        if (bodyStart > input.Length) return (0, false);

        var body = input.Slice(bodyStart);
        var closeIdx = body.IndexOf(closer);
        if (closeIdx < 0)
        {
            var safe = body.Length - (closer.Length - 1);
            if (safe < 0) safe = 0;
            return ((bodyStart - cursor) + safe, false);
        }
        return ((bodyStart - cursor) + closeIdx + closer.Length, true);
    }
}
