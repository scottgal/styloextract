namespace StyloExtract.Streaming;

/// <summary>
/// Tiny inline filter carrying the up-to-three tag-name hashes the active
/// scanner's tripwires care about (prefix / content-start / content-end).
///
/// Used by <see cref="MinimalHtmlTokenizer"/> /
/// <see cref="IncrementalHtmlTokenizer"/> to gate attribute extraction:
/// for every tag, the tokenizer hashes the tag name (cheap) and then asks
/// the filter whether this tag could possibly satisfy any tripwire. If
/// not, the tokenizer emits a tag-only event and skips the per-tag
/// <c>class</c> / <c>id</c> / <c>role</c> / <c>data-*</c> / <c>aria-*</c>
/// pass entirely — eliminating the dominant remaining allocation on the
/// streaming hot path.
///
/// On a real page the FSM rejects ~95%+ of tags on a single tag-hash
/// compare (most <c>div</c> / <c>span</c> / <c>a</c> / <c>img</c> aren't
/// tripwire targets), so attribute extraction was pure waste for them.
///
/// The default (zero-arg) value <see cref="MatchAll"/> matches every tag;
/// tokenizer consumers that don't have a scanner context (tests, ad-hoc
/// callers) get the previous behaviour unchanged.
/// </summary>
public readonly struct TripwireTagFilter
{
    private readonly ulong _h1;
    private readonly ulong _h2;
    private readonly ulong _h3;
    private readonly byte _count;

    /// <summary>Match every tag — the default; preserves pre-tuning behaviour.</summary>
    public static TripwireTagFilter MatchAll => default;

    public TripwireTagFilter(ulong h1)
    {
        _h1 = h1;
        _h2 = 0UL;
        _h3 = 0UL;
        _count = 1;
    }

    public TripwireTagFilter(ulong h1, ulong h2)
    {
        _h1 = h1;
        _h2 = h2;
        _h3 = 0UL;
        _count = 2;
    }

    public TripwireTagFilter(ulong h1, ulong h2, ulong h3)
    {
        _h1 = h1;
        _h2 = h2;
        _h3 = h3;
        _count = 3;
    }

    /// <summary>
    /// Build a filter from a template's three tripwires. Duplicates collapse
    /// (common: prefix is &lt;header&gt; and end is also &lt;header&gt;'s
    /// peer — only 2 distinct hashes matter). Empty / 0-hash slots are
    /// dropped so the predicate never falsely matches an unhashed event.
    /// </summary>
    public static TripwireTagFilter FromTemplate(in StreamingTemplate template)
    {
        var a = template.PrefixTripwire.TagHash;
        var b = template.ContentStartTripwire.TagHash;
        var c = template.ContentEndTripwire.TagHash;

        // Build a tiny dedup'd list.
        Span<ulong> kept = stackalloc ulong[3];
        int n = 0;
        AppendIfNovel(kept, ref n, a);
        AppendIfNovel(kept, ref n, b);
        AppendIfNovel(kept, ref n, c);

        return n switch
        {
            0 => MatchAll,
            1 => new TripwireTagFilter(kept[0]),
            2 => new TripwireTagFilter(kept[0], kept[1]),
            _ => new TripwireTagFilter(kept[0], kept[1], kept[2]),
        };
    }

    private static void AppendIfNovel(Span<ulong> kept, ref int n, ulong h)
    {
        if (h == 0UL) return;
        for (int i = 0; i < n; i++)
            if (kept[i] == h) return;
        kept[n++] = h;
    }

    /// <summary>
    /// True iff <paramref name="tagHash"/> could match an active tripwire.
    /// MatchAll (count==0) returns true for every tag — back-compat path.
    /// </summary>
    public bool Matches(ulong tagHash) =>
        _count == 0
        || tagHash == _h1
        || (_count > 1 && tagHash == _h2)
        || (_count > 2 && tagHash == _h3);
}