using System.IO.Hashing;

namespace StyloExtract.Streaming;

/// <summary>
/// Static set of pre-hashed structural HTML tag names that the fence
/// scanner cares about. Non-structural tags (meta, link, script chrome,
/// span, img, etc.) bypass the MinHash sketch entirely — they can't
/// meaningfully contribute to a chrome→content→chrome shape signal.
///
/// alpha.21 replaces the per-fence <c>TagAllowlistBloom</c> with this
/// static allowlist. The inducer already only picks structural tags as
/// fence markers; this just makes the filter explicit + universal.
/// </summary>
internal static class StructuralTagAllowlist
{
    // Whitelisted structural tags (lowercase, no angle brackets). Anything
    // not in this list skips the sketch push + recompute at the scanner level.
    private static readonly string[] s_names =
    {
        "html", "body", "head",
        "header", "footer", "nav", "aside",
        "section", "article", "main", "div",
        "p", "ul", "ol", "li",
        "h1", "h2", "h3", "h4", "h5", "h6",
        "table", "tbody", "thead", "tr",
    };

    private static readonly HashSet<ulong> s_hashes = BuildHashes();

    private static HashSet<ulong> BuildHashes()
    {
        var set = new HashSet<ulong>(s_names.Length);
        Span<byte> buf = stackalloc byte[16];
        foreach (var name in s_names)
        {
            var len = System.Text.Encoding.UTF8.GetBytes(name, buf);
            set.Add(XxHash3.HashToUInt64(buf[..len]));
        }
        return set;
    }

    /// <summary>True if <paramref name="tagNameHash"/> corresponds to a structural tag.</summary>
    public static bool Contains(ulong tagNameHash) => s_hashes.Contains(tagNameHash);
}
