namespace StyloExtract.Abstractions;

/// <summary>
/// Hash-only snapshot of an element's identity attributes — the lean
/// streaming-side counterpart of <see cref="ElementClaimSet"/>.
///
/// The layout side carries the full <see cref="ElementClaimSet"/> with
/// strings for diagnostics and round-tripping. The streaming scanner only
/// needs the hashes — it never serialises the snapshot, never logs the
/// raw class names, and lives on the per-tag hot path. <see cref="ElementHashSet"/>
/// gives the scanner an allocation-light handle to feed into
/// <see cref="IdentityClaimMatcher.MatchesByHash"/>.
///
/// Conventions:
/// - <see cref="TagHash"/> is required (every element has a tag).
/// - <see cref="IdHash"/> == 0 and <see cref="RoleHash"/> == 0 mean "absent".
///   (XxHash3 of any non-empty input is almost never 0; collisions here would
///    only affect optional fields and would degrade to "claim does not apply"
///    which is sound.)
/// - <see cref="ClassHashes"/> holds individual class-token hashes; empty
///   means "no classes".
/// - <see cref="DataAttrHashes"/> / <see cref="AriaAttrHashes"/> hold
///   precomputed (name, value) hash pairs for data-* / aria-* attributes
///   the tokenizer was willing to capture (typically a small prefix —
///   the scanner doesn't need every attribute, only the ones an identity
///   claim might reference).
/// </summary>
public readonly struct ElementHashSet
{
    public ulong TagHash { get; init; }
    public ulong IdHash { get; init; }
    public ulong RoleHash { get; init; }
    public IReadOnlyList<ulong> ClassHashes { get; init; }
    public IReadOnlyList<AttrHashPair> DataAttrHashes { get; init; }
    public IReadOnlyList<AttrHashPair> AriaAttrHashes { get; init; }

    public ElementHashSet(
        ulong tagHash,
        ulong idHash = 0UL,
        ulong roleHash = 0UL,
        IReadOnlyList<ulong>? classHashes = null,
        IReadOnlyList<AttrHashPair>? dataAttrHashes = null,
        IReadOnlyList<AttrHashPair>? ariaAttrHashes = null)
    {
        TagHash = tagHash;
        IdHash = idHash;
        RoleHash = roleHash;
        ClassHashes = classHashes ?? Array.Empty<ulong>();
        DataAttrHashes = dataAttrHashes ?? Array.Empty<AttrHashPair>();
        AriaAttrHashes = ariaAttrHashes ?? Array.Empty<AttrHashPair>();
    }
}

/// <summary>
/// Precomputed (attribute-name, attribute-value) hash pair for data-* /
/// aria-* identity matching. Name is the attribute name WITHOUT the
/// "data-" / "aria-" prefix; value is the attribute value verbatim — both
/// xxHash3 of the UTF-8 bytes.
/// </summary>
public readonly record struct AttrHashPair(ulong NameHash, ulong ValueHash);
