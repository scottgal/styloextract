using StyloExtract.Abstractions;

namespace StyloExtract.Streaming;

/// <summary>
/// One emitted tag event from the tokenizer. Carries enough hash data for
/// the tripwire matcher (Task 4 of Phase 1) to evaluate an
/// <see cref="IdentityClaim"/> against the element WITHOUT looking back at
/// the raw chunk bytes — by emit-time the tokenizer has already compacted
/// them out of its sliding-window buffer.
///
/// Field semantics:
/// - <see cref="TagNameHash"/>: xxHash3 of the lowercased tag name.
/// - <see cref="ClassHash"/>: xxHash3 of the WHOLE class attribute string
///   ("foo bar baz" → one hash). Kept for backward compatibility with
///   alpha.21..23 tokenizer consumers; the tripwire matcher does NOT use it.
/// - <see cref="ClassHashes"/>: xxHash3 of each individual class token.
///   What the tripwire matcher consumes via
///   <see cref="IdentityClaimMatcher.MatchesByHash"/>.
/// - <see cref="IdHash"/>: xxHash3 of the id attribute value, or 0 if absent.
/// - <see cref="RoleHash"/>: xxHash3 of the role attribute value, or 0
///   if absent.
/// - <see cref="DataAttrHashes"/> / <see cref="AriaAttrHashes"/>: the first
///   few data-* / aria-* attributes the tokenizer parsed, as
///   (name-hash, value-hash) pairs. Bounded to keep the per-event cost
///   constant; identity claims rarely reference more than a couple of
///   these attributes.
/// - <see cref="ByteLength"/>, <see cref="IsClose"/>: as before.
///
/// On close tags every hash field except <see cref="TagNameHash"/>,
/// <see cref="ByteLength"/>, and <see cref="IsClose"/> reads as empty
/// (close tags have no attributes).
/// </summary>
public readonly struct TagEvent
{
    public ulong TagNameHash { get; init; }
    public ulong ClassHash { get; init; }
    public ulong IdHash { get; init; }
    public ulong RoleHash { get; init; }
    public IReadOnlyList<ulong> ClassHashes { get; init; }
    public IReadOnlyList<AttrHashPair> DataAttrHashes { get; init; }
    public IReadOnlyList<AttrHashPair> AriaAttrHashes { get; init; }
    public int ByteLength { get; init; }
    public bool IsClose { get; init; }

    /// <summary>
    /// Construct an event with only the legacy alpha.21..23 fields. The
    /// extended hash fields default to empty — for synthetic test events
    /// that don't need to drive the tripwire matcher.
    /// </summary>
    public TagEvent(ulong tagNameHash, ulong classHash, int byteLength, bool isClose)
    {
        TagNameHash = tagNameHash;
        ClassHash = classHash;
        IdHash = 0UL;
        RoleHash = 0UL;
        ClassHashes = Array.Empty<ulong>();
        DataAttrHashes = Array.Empty<AttrHashPair>();
        AriaAttrHashes = Array.Empty<AttrHashPair>();
        ByteLength = byteLength;
        IsClose = isClose;
    }

    /// <summary>
    /// Build the lean <see cref="ElementHashSet"/> the tripwire matcher
    /// consumes. Reuses the precomputed hashes carried on the event —
    /// no string materialisation, no rehashing.
    /// </summary>
    public ElementHashSet ToElementHashSet() => new(
        tagHash: TagNameHash,
        idHash: IdHash,
        roleHash: RoleHash,
        classHashes: ClassHashes,
        dataAttrHashes: DataAttrHashes,
        ariaAttrHashes: AriaAttrHashes);
}
