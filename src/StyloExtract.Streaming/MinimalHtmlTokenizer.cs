using System.IO.Hashing;

namespace StyloExtract.Streaming;

public ref struct MinimalHtmlTokenizer
{
    private static readonly ulong s_scriptHash = XxHash3.HashToUInt64("script"u8);
    private static readonly ulong s_styleHash = XxHash3.HashToUInt64("style"u8);

    private readonly ReadOnlySpan<byte> _input;
    private int _position;

    public MinimalHtmlTokenizer(ReadOnlySpan<byte> input)
    {
        _input = input;
        _position = 0;
    }

    public bool TryReadTag(out TagEvent evt)
    {
        evt = default;
        if (_position >= _input.Length) return false;

        var remaining = _input.Slice(_position);
        var ltIdx = remaining.IndexOf((byte)'<');
        if (ltIdx < 0)
        {
            _position = _input.Length;
            return false;
        }

        var afterLt = _position + ltIdx + 1;
        if (afterLt >= _input.Length) return false;

        var isClose = _input[afterLt] == (byte)'/';
        var nameStart = isClose ? afterLt + 1 : afterLt;
        if (nameStart >= _input.Length) return false;

        var tagContent = _input.Slice(nameStart);
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
        var classHash = isClose ? 0UL : ExtractClassHash(inner.Slice(nameLen));
        var tagStart = _position + ltIdx;
        var tagEnd = nameStart + gtIdx + 1;
        evt = new TagEvent(nameHash, classHash, ByteLength: tagEnd - tagStart, IsClose: isClose);
        _position = tagEnd;

        if (!isClose)
        {
            if (nameHash == s_scriptHash) SkipBodyTo("</script>"u8);
            else if (nameHash == s_styleHash) SkipBodyTo("</style>"u8);
        }
        return true;
    }

    private void SkipBodyTo(ReadOnlySpan<byte> closeTag)
    {
        var remaining = _input.Slice(_position);
        var idx = remaining.IndexOf(closeTag);
        if (idx < 0) _position = _input.Length;
        else _position += idx;
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
