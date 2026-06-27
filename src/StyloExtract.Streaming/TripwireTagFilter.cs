namespace StyloExtract.Streaming;

/// <summary>
/// Tiny inline tag-name-hash filter retained from the Task 4 tripwire era as
/// a hot-path attribute-extraction gate for <see cref="MinimalHtmlTokenizer"/>
/// and <see cref="IncrementalHtmlTokenizer"/>. Task 13 of Phase 1 moved the
/// streaming scanner off these tokenizers entirely; the filter is now a
/// best-effort opt-in for tokeniser consumers that already know which tag
/// names they care about. The default (zero-arg) value
/// <see cref="MatchAll"/> matches every tag — the path most callers take
/// post-Task-13.
/// </summary>
public readonly struct TripwireTagFilter
{
    private readonly ulong _h1;
    private readonly ulong _h2;
    private readonly ulong _h3;
    private readonly byte _count;

    /// <summary>Match every tag — the default.</summary>
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
    /// True iff <paramref name="tagHash"/> could match an active tag-name
    /// constraint. <see cref="MatchAll"/> (count==0) returns true for every
    /// tag.
    /// </summary>
    public bool Matches(ulong tagHash) =>
        _count == 0
        || tagHash == _h1
        || (_count > 1 && tagHash == _h2)
        || (_count > 2 && tagHash == _h3);
}
