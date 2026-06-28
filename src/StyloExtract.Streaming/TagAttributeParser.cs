using System.IO.Hashing;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// Span-based extractor for the attribute slice of a parsed tag. Pulls out
/// the precomputed hashes the tripwire matcher needs:
///   - <c>class</c> token-by-token (capped at <c>TagAttrLimits.MaxClassesPerEvent</c>)
///   - <c>id</c> (single hash)
///   - <c>role</c> (single hash)
///   - <c>data-*</c> (capped at <c>TagAttrLimits.MaxAttrPairsPerEvent</c>)
///   - <c>aria-*</c> (capped at <c>TagAttrLimits.MaxAttrPairsPerEvent</c>)
///
/// Caps are per-tokenizer-instance, threaded down via
/// <see cref="TagAttrLimits"/>. They bound the parser's stackalloc scratch
/// buffers, NOT the system's tolerance for attribute-heavy markup: the
/// defaults (32 classes / 16 attr-pairs) cover everything observed in
/// dogfood corpora. Raise via <see cref="StreamingTokenizerOptions"/> if a
/// host genuinely needs more.
///
/// Allocation: the output arrays are pooled-empty for the zero-attr case
/// (the majority of HTML tags) via <c>Array.Empty</c>. Tags with attributes
/// allocate one small array per populated kind (class / data / aria).
/// </summary>
internal static class TagAttributeParser
{
    private static readonly AttrHashPair[] s_emptyPairs = Array.Empty<AttrHashPair>();
    private static readonly ulong[] s_emptyHashes = Array.Empty<ulong>();

    /// <summary>
    /// Backward-compatible alpha.21..23 helper: hash of the WHOLE class
    /// attribute string. Kept so the legacy <c>TagEvent.ClassHash</c> field
    /// stays populated for any consumer that relied on it.
    /// </summary>
    public static ulong ExtractClassHash(ReadOnlySpan<byte> attrs)
    {
        if (!TryFindAttrValue(attrs, "class"u8, out var value)) return 0UL;
        return XxHash3.HashToUInt64(value);
    }

    /// <summary>
    /// Extract every identity-relevant hash from <paramref name="attrs"/> in
    /// one pass. Allocates only for the attribute kinds the tag actually
    /// carries; tags with no class/id/role/data-/aria- attribute reuse
    /// shared empty arrays.
    /// </summary>
    public static void ExtractIdentityHashes(
        ReadOnlySpan<byte> attrs,
        TagAttrLimits limits,
        out ulong idHash,
        out ulong roleHash,
        out ulong[] classHashes,
        out AttrHashPair[] dataAttrs,
        out AttrHashPair[] ariaAttrs)
    {
        idHash = 0UL;
        roleHash = 0UL;
        classHashes = s_emptyHashes;
        dataAttrs = s_emptyPairs;
        ariaAttrs = s_emptyPairs;

        if (attrs.IsEmpty) return;

        Span<ulong> classBuf = stackalloc ulong[limits.MaxClassesPerEvent];
        Span<AttrHashPair> dataBuf = stackalloc AttrHashPair[limits.MaxAttrPairsPerEvent];
        Span<AttrHashPair> ariaBuf = stackalloc AttrHashPair[limits.MaxAttrPairsPerEvent];
        int classCount = 0, dataCount = 0, ariaCount = 0;

        // Single left-to-right scan, lifting one (name, value) attribute per
        // iteration. Quotes can be single or double; bare-value attrs
        // (no quotes) are uncommon in modern HTML and skipped — the
        // tokenizer was lenient about them before, the matcher follows suit.
        int i = 0;
        while (i < attrs.Length)
        {
            // Skip whitespace.
            while (i < attrs.Length && IsWs(attrs[i])) i++;
            if (i >= attrs.Length) break;

            // Attribute name: chars up to '=' or whitespace.
            int nameStart = i;
            while (i < attrs.Length
                   && attrs[i] != (byte)'='
                   && !IsWs(attrs[i])
                   && attrs[i] != (byte)'/'
                   && attrs[i] != (byte)'>')
                i++;
            int nameEnd = i;
            if (nameEnd == nameStart) { i++; continue; }
            var name = attrs.Slice(nameStart, nameEnd - nameStart);

            // Skip whitespace + optional '='.
            while (i < attrs.Length && IsWs(attrs[i])) i++;
            if (i >= attrs.Length || attrs[i] != (byte)'=')
            {
                // Boolean attribute (no value). Doesn't contribute to
                // identity matching; move on.
                continue;
            }
            i++; // consume '='
            while (i < attrs.Length && IsWs(attrs[i])) i++;
            if (i >= attrs.Length) break;

            var quote = attrs[i];
            if (quote != (byte)'"' && quote != (byte)'\'')
            {
                // Bare value — skip to next whitespace.
                int bareStart = i;
                while (i < attrs.Length && !IsWs(attrs[i]) && attrs[i] != (byte)'>') i++;
                _ = attrs.Slice(bareStart, i - bareStart);
                continue;
            }
            i++; // consume opening quote
            int valueStart = i;
            while (i < attrs.Length && attrs[i] != quote) i++;
            if (i >= attrs.Length) break;
            var value = attrs.Slice(valueStart, i - valueStart);
            i++; // consume closing quote

            // Dispatch on attribute kind.
            if (Eq(name, "class"u8))
            {
                if (classCount < classBuf.Length)
                    classCount = SplitClassesIntoBuffer(value, classBuf, classCount);
            }
            else if (Eq(name, "id"u8))
            {
                if (!value.IsEmpty) idHash = XxHash3.HashToUInt64(value);
            }
            else if (Eq(name, "role"u8))
            {
                if (!value.IsEmpty) roleHash = XxHash3.HashToUInt64(value);
            }
            else if (StartsWith(name, "data-"u8) && name.Length > 5)
            {
                if (dataCount < dataBuf.Length)
                {
                    var rawName = name.Slice(5);
                    dataBuf[dataCount++] = new AttrHashPair(
                        XxHash3.HashToUInt64(rawName),
                        XxHash3.HashToUInt64(value));
                }
            }
            else if (StartsWith(name, "aria-"u8) && name.Length > 5)
            {
                if (ariaCount < ariaBuf.Length)
                {
                    var rawName = name.Slice(5);
                    ariaBuf[ariaCount++] = new AttrHashPair(
                        XxHash3.HashToUInt64(rawName),
                        XxHash3.HashToUInt64(value));
                }
            }
        }

        if (classCount > 0)
        {
            classHashes = new ulong[classCount];
            classBuf[..classCount].CopyTo(classHashes);
        }
        if (dataCount > 0)
        {
            dataAttrs = new AttrHashPair[dataCount];
            dataBuf[..dataCount].CopyTo(dataAttrs);
        }
        if (ariaCount > 0)
        {
            ariaAttrs = new AttrHashPair[ariaCount];
            ariaBuf[..ariaCount].CopyTo(ariaAttrs);
        }
    }

    /// <summary>
    /// Split a "foo bar baz" class-attribute value into individual class
    /// hashes appended to <paramref name="buf"/> starting at
    /// <paramref name="start"/>. Returns the new write index. Stops cleanly
    /// when the buffer fills up.
    /// </summary>
    private static int SplitClassesIntoBuffer(ReadOnlySpan<byte> value, Span<ulong> buf, int start)
    {
        int write = start;
        int i = 0;
        while (i < value.Length && write < buf.Length)
        {
            // Skip whitespace.
            while (i < value.Length && IsWs(value[i])) i++;
            if (i >= value.Length) break;
            int tokStart = i;
            while (i < value.Length && !IsWs(value[i])) i++;
            var token = value.Slice(tokStart, i - tokStart);
            if (!token.IsEmpty)
                buf[write++] = XxHash3.HashToUInt64(token);
        }
        return write;
    }

    private static bool TryFindAttrValue(ReadOnlySpan<byte> attrs, ReadOnlySpan<byte> attrName, out ReadOnlySpan<byte> value)
    {
        int i = 0;
        while (i < attrs.Length)
        {
            var slice = attrs.Slice(i);
            var idx = slice.IndexOf(attrName);
            if (idx < 0) break;
            int abs = i + idx;
            if (IsAttrNameBoundary(attrs, abs) && abs + attrName.Length < attrs.Length)
            {
                int after = abs + attrName.Length;
                // Skip whitespace.
                while (after < attrs.Length && IsWs(attrs[after])) after++;
                if (after < attrs.Length && attrs[after] == (byte)'=')
                {
                    after++;
                    while (after < attrs.Length && IsWs(attrs[after])) after++;
                    if (after < attrs.Length)
                    {
                        var quote = attrs[after];
                        if (quote == (byte)'"' || quote == (byte)'\'')
                        {
                            after++;
                            int valStart = after;
                            while (after < attrs.Length && attrs[after] != quote) after++;
                            if (after <= attrs.Length)
                            {
                                value = attrs.Slice(valStart, after - valStart);
                                return true;
                            }
                        }
                    }
                }
            }
            i = abs + attrName.Length;
        }
        value = default;
        return false;
    }

    private static bool IsAttrNameBoundary(ReadOnlySpan<byte> attrs, int pos)
    {
        if (pos == 0) return true;
        var prev = attrs[pos - 1];
        return prev == (byte)' ' || prev == (byte)'\t' || prev == (byte)'\n' || prev == (byte)'\r';
    }

    private static bool Eq(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.Length == b.Length && a.SequenceEqual(b);

    private static bool StartsWith(ReadOnlySpan<byte> a, ReadOnlySpan<byte> prefix) =>
        a.Length >= prefix.Length && a.Slice(0, prefix.Length).SequenceEqual(prefix);

    private static bool IsWs(byte b) =>
        b == (byte)' ' || b == (byte)'\t' || b == (byte)'\n' || b == (byte)'\r';
}
