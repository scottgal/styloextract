using System.IO.Hashing;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Pure scoring function over an <see cref="ElementClaimSet"/>. Mirrors the
/// finder.js penalty schedule (id=0, class=1, data-*=2, aria-*=2, role=2,
/// tag=5). Picks the single best identity claim for an element by walking
/// penalty tiers low-to-high and returning the first match.
///
/// No positional axes ever emitted (no nth-of-type / nth-child) - if no
/// identity attribute is stable, the scorer returns a tag-only claim and
/// expects the caller to disambiguate via an ancestor chain.
/// </summary>
public static class SelectorPenaltyScorer
{
    public const int PenaltyId = 0;
    public const int PenaltyClass = 1;
    public const int PenaltyAttribute = 2;
    public const int PenaltyRole = 2;
    public const int PenaltyTagOnly = 5;

    /// <summary>
    /// Returns the smallest-penalty single-element claim derivable from
    /// <paramref name="element"/>. When <paramref name="bestClass"/> is
    /// supplied it overrides the default "first stable class" pick - the
    /// caller can compute a most-specific class via document frequency
    /// and feed it back in here.
    /// </summary>
    public static (IdentityClaim Claim, int Penalty) PickBest(
        ElementClaimSet element,
        string? bestClass = null)
    {
        // Tier 0: id.
        if (element.Id is { } id && element.IdHash is ulong idHash)
        {
            return (BuildIdClaim(element, id, idHash), PenaltyId);
        }

        // Tier 1: class.
        if (element.Classes.Count > 0)
        {
            var chosen = bestClass is not null && element.Classes.Contains(bestClass)
                ? bestClass
                : element.Classes[0];
            var idx = -1;
            for (var i = 0; i < element.Classes.Count; i++)
            {
                if (element.Classes[i] == chosen) { idx = i; break; }
            }
            var hash = idx >= 0 ? element.ClassHashes[idx] : H(chosen);
            return (BuildClassClaim(element, chosen, hash), PenaltyClass);
        }

        // Tier 2a: data-* (first-by-name for stable picking).
        if (element.DataAttrs.Count > 0)
        {
            var kv = FirstStableKvp(element.DataAttrs);
            return (BuildDataClaim(element, kv.Key, kv.Value), PenaltyAttribute);
        }

        // Tier 2b: aria-*.
        if (element.AriaAttrs.Count > 0)
        {
            var kv = FirstStableKvp(element.AriaAttrs);
            return (BuildAriaClaim(element, kv.Key, kv.Value), PenaltyAttribute);
        }

        // Tier 2c: role.
        if (element.Role is { } role)
        {
            return (BuildRoleClaim(element, role), PenaltyRole);
        }

        // Tier 5: tag only.
        return (BuildTagOnlyClaim(element), PenaltyTagOnly);
    }

    private static KeyValuePair<string, string> FirstStableKvp(
        IReadOnlyDictionary<string, string> dict)
    {
        // Iteration order is preserved by the upstream IdentityClaimExtractor,
        // which copies attributes in source order. Pick the first - any attribute
        // we kept is by definition "stable" (the extractor doesn't filter on
        // data-/aria- values yet, only on class hash-shape).
        foreach (var kv in dict) return kv;
        // Empty case is guarded by the caller, but keep the compiler happy.
        return default;
    }

    private static IdentityClaim BuildIdClaim(ElementClaimSet el, string id, ulong idHash) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
        Id = id,
        IdHash = idHash,
    };

    private static IdentityClaim BuildClassClaim(ElementClaimSet el, string cls, ulong hash) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
        Classes = new[] { cls },
        ClassHashes = new[] { hash },
    };

    private static IdentityClaim BuildDataClaim(ElementClaimSet el, string key, string value) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
        DataAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value },
    };

    private static IdentityClaim BuildAriaClaim(ElementClaimSet el, string key, string value) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
        AriaAttrs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [key] = value },
    };

    private static IdentityClaim BuildRoleClaim(ElementClaimSet el, string role) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
        Role = role,
    };

    private static IdentityClaim BuildTagOnlyClaim(ElementClaimSet el) => new()
    {
        Tag = el.Tag,
        TagHash = el.TagHash,
    };

    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));
}
