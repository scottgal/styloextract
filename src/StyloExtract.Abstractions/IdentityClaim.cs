namespace StyloExtract.Abstractions;

/// <summary>
/// A conjunction of identity claims about an element. The element matches the
/// claim if and only if every non-null field holds. <see cref="Tag"/> is the
/// only required field (every claim picks elements by tag at minimum).
///
/// Hash variants are precomputed at emission time so the matcher can compare
/// against incoming TagEvent hashes (streaming) or against extracted element
/// claim sets (layout) without re-hashing per comparison.
/// </summary>
public sealed record IdentityClaim
{
    /// <summary>Lowercase tag name. Required.</summary>
    public required string Tag { get; init; }

    /// <summary>XxHash3 of <see cref="Tag"/> for fast comparison.</summary>
    public required ulong TagHash { get; init; }

    /// <summary>Optional element id. When non-null the element's id must match.</summary>
    public string? Id { get; init; }

    /// <summary>XxHash3 of <see cref="Id"/> when present.</summary>
    public ulong? IdHash { get; init; }

    /// <summary>Required classes the element must carry. Order-insensitive.
    /// Empty array means "no class requirement".</summary>
    public IReadOnlyList<string> Classes { get; init; } = Array.Empty<string>();

    /// <summary>XxHash3 of each class for fast comparison.</summary>
    public IReadOnlyList<ulong> ClassHashes { get; init; } = Array.Empty<ulong>();

    /// <summary>data-* attributes that must match. Key is the attribute name
    /// WITHOUT the "data-" prefix.</summary>
    public IReadOnlyDictionary<string, string> DataAttrs { get; init; } =
        new Dictionary<string, string>();

    /// <summary>aria-* attributes that must match. Key is the attribute name
    /// WITHOUT the "aria-" prefix.</summary>
    public IReadOnlyDictionary<string, string> AriaAttrs { get; init; } =
        new Dictionary<string, string>();

    /// <summary>role attribute that must match (case-insensitive).</summary>
    public string? Role { get; init; }
}
