using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Hand-rolled byte-level matcher for one <see cref="BytePattern"/>. Pure
/// static, span-based, zero-allocation.
///
/// Encoding choice: a hand-rolled state machine over the input span rather
/// than an Aho-Corasick automaton or a lookup table. The pattern shape is
/// fixed (<c>&lt;tag attrs&gt;</c>) and the variability lives entirely
/// inside the attribute scan, so a small switch over named states reads
/// cleaner than a precomputed transition table and still compiles to tight
/// branchy code with no per-byte allocation. Aho-Corasick wins for
/// multi-pattern parallel search; here each scanner state only watches one
/// pattern at a time, so the multi-pattern machinery has no payoff.
///
/// <see cref="TryMatchAt"/> distinguishes three outcomes via an out-flag:
/// matched (positive bytes consumed), definitively-no-match (0 consumed,
/// <c>needsMore=false</c>), or matched-the-prefix-but-ran-out-of-bytes
/// (0 consumed, <c>needsMore=true</c>). The chunked scanner uses the third
/// case to decide when to carry over.
/// </summary>
internal static class BytePatternDfa
{
    /// <summary>
    /// Try to match <paramref name="pattern"/> at <paramref name="position"/>
    /// inside <paramref name="input"/>. Returns the number of bytes consumed
    /// on success; 0 on failure. <paramref name="needsMore"/> indicates
    /// whether the failure was "definitively no match" (false) or "matched
    /// the prefix but ran out of input" (true). Position must point at the
    /// leading <c>&lt;</c>.
    /// </summary>
    public static int TryMatchAt(ReadOnlySpan<byte> input, int position, BytePattern pattern, out bool needsMore)
    {
        needsMore = false;
        if ((uint)position >= (uint)input.Length) { needsMore = true; return 0; }
        if (input[position] != (byte)'<') return 0;

        int cursor = position + 1;
        if (pattern.IsClose)
        {
            if (cursor >= input.Length) { needsMore = true; return 0; }
            if (input[cursor] != (byte)'/') return 0;
            cursor++;
        }
        else
        {
            if (cursor >= input.Length) { needsMore = true; return 0; }
            if (input[cursor] == (byte)'/') return 0;
        }

        var tagName = pattern.TagNameSpan;
        if (cursor + tagName.Length > input.Length)
        {
            // We don't have the full tag name yet. Could still match if more
            // bytes arrive — but only if the partial prefix matches.
            var have = input.Length - cursor;
            if (have <= 0) { needsMore = true; return 0; }
            if (!input.Slice(cursor, have).SequenceEqual(tagName.Slice(0, have))) return 0;
            needsMore = true;
            return 0;
        }
        if (!input.Slice(cursor, tagName.Length).SequenceEqual(tagName)) return 0;
        cursor += tagName.Length;

        if (cursor >= input.Length) { needsMore = true; return 0; }
        if (!IsTagNameTerminator(input[cursor])) return 0;

        var scanEnd = Math.Min(input.Length, position + pattern.MaxScanBytes);

        // Close-tag form: no attributes; walk to '>' over whitespace only.
        if (pattern.IsClose)
        {
            while (cursor < scanEnd)
            {
                if (input[cursor] == (byte)'>') return (cursor + 1) - position;
                if (!IsWhitespace(input[cursor])) return 0;
                cursor++;
            }
            // Ran out of bytes (either real EOI or hit scanEnd cap).
            if (scanEnd == input.Length) needsMore = true;
            return 0;
        }

        var attrs = pattern.AttrsSpan;
        ulong attrsMask = 0UL;
        int attrsSatisfied = 0;
        byte quote = 0;

        while (cursor < scanEnd)
        {
            var b = input[cursor];

            if (quote != 0)
            {
                if (b == quote) quote = 0;
                cursor++;
                continue;
            }

            if (b == (byte)'>')
            {
                if (attrsSatisfied != attrs.Length) return 0;
                return (cursor + 1) - position;
            }

            if (b == (byte)'"' || b == (byte)'\'')
            {
                quote = b;
                cursor++;
                continue;
            }

            if (IsWhitespace(b) || b == (byte)'/')
            {
                cursor++;
                continue;
            }

            // Non-whitespace at attribute-name position. Try each unsatisfied
            // attribute.
            bool matched = false;
            if (attrs.Length > 0)
            {
                for (int i = 0; i < attrs.Length; i++)
                {
                    var bit = 1UL << i;
                    if ((attrsMask & bit) != 0) continue;
                    var consumed = TryMatchAttr(input.Slice(cursor), attrs[i], out var attrNeedsMore);
                    if (consumed > 0)
                    {
                        attrsMask |= bit;
                        attrsSatisfied++;
                        cursor += consumed;
                        matched = true;
                        break;
                    }
                    if (attrNeedsMore && scanEnd == input.Length)
                    {
                        needsMore = true;
                        return 0;
                    }
                }
            }
            if (matched) continue;

            cursor = SkipUnknownAttribute(input, cursor, scanEnd, out var skipNeedsMore);
            if (cursor < 0)
            {
                if (skipNeedsMore && scanEnd == input.Length) needsMore = true;
                return 0;
            }
        }

        if (scanEnd == input.Length) needsMore = true;
        return 0;
    }

    /// <summary>
    /// Advance past a single attribute the matcher doesn't care about. Returns
    /// the new cursor position, or -1 on incomplete input.
    /// </summary>
    private static int SkipUnknownAttribute(
        ReadOnlySpan<byte> input,
        int cursor,
        int scanEnd,
        out bool needsMore)
    {
        needsMore = false;
        while (cursor < scanEnd)
        {
            var b = input[cursor];
            if (IsWhitespace(b) || b == (byte)'=' || b == (byte)'/' || b == (byte)'>') break;
            cursor++;
        }
        if (cursor >= scanEnd) { needsMore = true; return -1; }

        while (cursor < scanEnd && IsWhitespace(input[cursor])) cursor++;
        if (cursor >= scanEnd) { needsMore = true; return -1; }
        if (input[cursor] != (byte)'=') return cursor;
        cursor++;
        while (cursor < scanEnd && IsWhitespace(input[cursor])) cursor++;
        if (cursor >= scanEnd) { needsMore = true; return -1; }

        var first = input[cursor];
        if (first == (byte)'"' || first == (byte)'\'')
        {
            cursor++;
            while (cursor < scanEnd && input[cursor] != first) cursor++;
            if (cursor >= scanEnd) { needsMore = true; return -1; }
            return cursor + 1;
        }

        while (cursor < scanEnd)
        {
            var b = input[cursor];
            if (IsWhitespace(b) || b == (byte)'/' || b == (byte)'>') break;
            cursor++;
        }
        return cursor;
    }

    /// <summary>
    /// Match one attribute at <paramref name="input"/> position 0. Returns
    /// bytes consumed on success; 0 on failure. <paramref name="needsMore"/>
    /// distinguishes "definitively no match" from "ran out of bytes while
    /// matching".
    /// </summary>
    private static int TryMatchAttr(ReadOnlySpan<byte> input, AttrConstraint attr, out bool needsMore)
    {
        needsMore = false;
        var name = attr.NameSpan;
        if (input.Length < name.Length + 2)
        {
            // Could still match if more bytes arrive — but only if what we
            // do have matches.
            var have = Math.Min(input.Length, name.Length);
            if (have > 0 && !input.Slice(0, have).SequenceEqual(name.Slice(0, have))) return 0;
            needsMore = true;
            return 0;
        }
        if (!input.Slice(0, name.Length).SequenceEqual(name)) return 0;

        int cursor = name.Length;
        if (cursor >= input.Length) { needsMore = true; return 0; }
        if (!IsAttrNameTerminator(input[cursor])) return 0;

        while (cursor < input.Length && IsWhitespace(input[cursor])) cursor++;
        if (cursor >= input.Length) { needsMore = true; return 0; }
        if (input[cursor] != (byte)'=') return 0;
        cursor++;
        while (cursor < input.Length && IsWhitespace(input[cursor])) cursor++;
        if (cursor >= input.Length) { needsMore = true; return 0; }

        var value = attr.ValueSpan;
        var first = input[cursor];
        if (first == (byte)'"' || first == (byte)'\'')
        {
            var q = first;
            cursor++;
            if (cursor + value.Length > input.Length) { needsMore = true; return 0; }
            if (!input.Slice(cursor, value.Length).SequenceEqual(value)) return 0;
            cursor += value.Length;
            if (cursor >= input.Length) { needsMore = true; return 0; }
            if (input[cursor] != q) return 0;
            cursor++;
            return cursor;
        }

        if (cursor + value.Length > input.Length) { needsMore = true; return 0; }
        if (!input.Slice(cursor, value.Length).SequenceEqual(value)) return 0;
        cursor += value.Length;
        if (cursor >= input.Length) { needsMore = true; return 0; }
        if (!IsAttrValueTerminator(input[cursor])) return 0;
        return cursor;
    }

    private static bool IsTagNameTerminator(byte b) =>
        IsWhitespace(b) || b == (byte)'/' || b == (byte)'>';

    private static bool IsAttrNameTerminator(byte b) =>
        IsWhitespace(b) || b == (byte)'=' || b == (byte)'/' || b == (byte)'>';

    private static bool IsAttrValueTerminator(byte b) =>
        IsWhitespace(b) || b == (byte)'/' || b == (byte)'>';

    private static bool IsWhitespace(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r' || b == (byte)'\f';
}
