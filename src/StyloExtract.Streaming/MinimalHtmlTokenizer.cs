using System.IO.Hashing;
using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

public ref struct MinimalHtmlTokenizer
{
    private static readonly ulong s_scriptHash = XxHash3.HashToUInt64("script"u8);
    private static readonly ulong s_styleHash = XxHash3.HashToUInt64("style"u8);

    private readonly ReadOnlySpan<byte> _input;
    private readonly TripwireTagFilter _filter;
    private readonly TagAttrLimits _attrLimits;
    private int _position;

    public MinimalHtmlTokenizer(ReadOnlySpan<byte> input)
        : this(input, TripwireTagFilter.MatchAll, TagAttrLimits.Default)
    {
    }

    public MinimalHtmlTokenizer(ReadOnlySpan<byte> input, TripwireTagFilter filter)
        : this(input, filter, TagAttrLimits.Default)
    {
    }

    public MinimalHtmlTokenizer(ReadOnlySpan<byte> input, TripwireTagFilter filter, TagAttrLimits attrLimits)
    {
        _input = input;
        _filter = filter;
        _attrLimits = attrLimits;
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
            var tagStart = _position + ltIdx;
            var tagEnd = nameStart + gtIdx + 1;

            // Tripwire prefilter: skip the per-tag attribute pass for tags
            // whose name-hash can't satisfy any active tripwire. The FSM
            // still needs every tag event for depth tracking, but the matcher
            // would have rejected this tag on the first tag-hash compare —
            // so the class/id/role/data/aria extraction (and its small heap
            // allocation per tag) was pure waste on ~95% of real-page tags.
            // The legacy ClassHash field stays populated only when the tag
            // is "interesting" to avoid the parallel attr scan as well.
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
                    attrs,
                    _attrLimits,
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
