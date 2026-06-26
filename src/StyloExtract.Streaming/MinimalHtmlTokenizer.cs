using System.IO.Hashing;

namespace StyloExtract.Streaming;

public ref struct MinimalHtmlTokenizer
{
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
        var tagStart = _position + ltIdx;
        var tagEnd = nameStart + gtIdx + 1;
        evt = new TagEvent(nameHash, ClassHash: 0, ByteLength: tagEnd - tagStart, IsClose: isClose);
        _position = tagEnd;
        return true;
    }
}
