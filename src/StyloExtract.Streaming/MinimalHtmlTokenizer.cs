using System.IO.Hashing;
using StyloExtract.Abstractions;

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
        while (_position < _input.Length)
        {
            var remaining = _input.Slice(_position);
            var ltIdx = remaining.IndexOf((byte)'<');
            if (ltIdx < 0)
            {
                _position = _input.Length;
                break;
            }

            var afterLt = _position + ltIdx + 1;
            if (afterLt >= _input.Length) break;

            if (IsCommentStart(afterLt))
            {
                _position = afterLt + 3;
                var rest = _input.Slice(_position);
                var endIdx = rest.IndexOf("-->"u8);
                if (endIdx < 0) { _position = _input.Length; break; }
                _position += endIdx + 3;
                continue;
            }

            var isClose = _input[afterLt] == (byte)'/';
            var nameStart = isClose ? afterLt + 1 : afterLt;
            if (nameStart >= _input.Length) break;

            var tagContent = _input.Slice(nameStart);
            var gtIdx = tagContent.IndexOf((byte)'>');
            if (gtIdx < 0) break;

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
            var classHash = isClose ? 0UL : TagAttributeParser.ExtractClassHash(attrs);
            var tagStart = _position + ltIdx;
            var tagEnd = nameStart + gtIdx + 1;

            ulong idHash = 0UL;
            ulong roleHash = 0UL;
            ulong[] classHashes = Array.Empty<ulong>();
            AttrHashPair[] dataAttrs = Array.Empty<AttrHashPair>();
            AttrHashPair[] ariaAttrs = Array.Empty<AttrHashPair>();
            if (!isClose && !attrs.IsEmpty)
            {
                TagAttributeParser.ExtractIdentityHashes(
                    attrs,
                    out idHash,
                    out roleHash,
                    out classHashes,
                    out dataAttrs,
                    out ariaAttrs);
            }

            evt = new TagEvent
            {
                TagNameHash = nameHash,
                ClassHash = classHash,
                IdHash = idHash,
                RoleHash = roleHash,
                ClassHashes = classHashes,
                DataAttrHashes = dataAttrs,
                AriaAttrHashes = ariaAttrs,
                ByteLength = tagEnd - tagStart,
                IsClose = isClose,
            };
            _position = tagEnd;

            if (!isClose)
            {
                if (nameHash == s_scriptHash) SkipBodyTo("</script>"u8);
                else if (nameHash == s_styleHash) SkipBodyTo("</style>"u8);
            }
            return true;
        }
        evt = default;
        return false;
    }

    private readonly bool IsCommentStart(int afterLt) =>
        afterLt + 2 < _input.Length
        && _input[afterLt] == (byte)'!'
        && _input[afterLt + 1] == (byte)'-'
        && _input[afterLt + 2] == (byte)'-';

    private void SkipBodyTo(ReadOnlySpan<byte> closeTag)
    {
        var remaining = _input.Slice(_position);
        var idx = remaining.IndexOf(closeTag);
        if (idx < 0) _position = _input.Length;
        else _position += idx;
    }
}
