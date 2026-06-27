namespace StyloExtract.Abstractions;

/// <summary>
/// Heuristic distance between two <see cref="IdentityClaim"/> chains (the shape
/// stored in <c>BlockRule.Claims</c> and <c>TemplateObservation.Claims</c>).
/// Larger value = more dissimilar.
///
/// Comparison aligns from the LEAF (target) end backwards. The shorter chain is
/// padded with virtual empty claims so alignment is well-defined; padded
/// positions accrue a per-position cost equal to the missing-claim penalty.
///
/// Per-position penalties (highest-cost first):
/// <list type="bullet">
///   <item>Tag mismatch: <see cref="TagMismatchPenalty"/>. Different tag means
///   different element identity, so this dominates everything else at that
///   position.</item>
///   <item>Both have an Id, ids differ: <see cref="IdMismatchPenalty"/>. Id is
///   meant to be unique-by-purpose; divergence is a strong negative signal.</item>
///   <item>Both have an Id, ids match: that position contributes 0 (id is
///   dispositive — classes / data-attr differences are ignored once both sides
///   agree on a unique id).</item>
///   <item>Class symmetric-difference: each class present in exactly one side
///   contributes <c>1 / (combinedSetSize + 1)</c>. Big class lists are
///   discounted so a 10-class element doesn't dominate distance by accident.</item>
///   <item>data-* / aria-* / role symmetric difference: <see cref="AttrMismatchPenalty"/>
///   per differing key (or differing value for a shared key).</item>
///   <item>One claim has Id/role and the other doesn't:
///   <see cref="OneSidedSpecificityPenalty"/> (half the symmetric mismatch
///   cost — one chain is more specific, not identity-divergent).</item>
/// </list>
///
/// Position weighting: leaf weight is 1.0, ancestor weights decay exponentially
/// by <see cref="PositionDecay"/> per step up. The leaf dominates because it's
/// the strongest identity anchor; ancestor differences are softer context.
///
/// Symmetric: <c>Compute(a, b) == Compute(b, a)</c>. Not a strict mathematical
/// metric (triangle inequality is approximate). Callers can normalize by
/// dividing by max-possible if a [0, 1] interval is needed.
/// </summary>
public static class SelectorDistance
{
    /// <summary>Penalty when both claims specify a tag and they differ.
    /// Set high so tag mismatch dominates other per-position contributions.</summary>
    public const double TagMismatchPenalty = 10.0;

    /// <summary>Penalty when both claims specify an Id and the ids differ.
    /// Ids are unique-by-purpose, so divergence is a strong identity signal.</summary>
    public const double IdMismatchPenalty = 5.0;

    /// <summary>Per-key cost for a data-*/aria-*/role symmetric-difference
    /// element (present-on-one-side or shared-key-different-value).</summary>
    public const double AttrMismatchPenalty = 1.0;

    /// <summary>Cost applied when one claim has an attribute-class (Id, role)
    /// the other lacks. Half the symmetric mismatch cost: one chain is more
    /// specific, not strictly different.</summary>
    public const double OneSidedSpecificityPenalty = 0.5;

    /// <summary>Cost contributed by a padding position (one chain longer than
    /// the other at that depth). Treated as a one-sided specificity gap
    /// scaled by the position weight.</summary>
    public const double PaddingPenalty = 1.0;

    /// <summary>Exponential position-weight decay per step up the ancestor
    /// chain from the leaf. Leaf = 1.0, parent = 0.5, grandparent = 0.25, ...</summary>
    public const double PositionDecay = 0.5;

    /// <summary>
    /// Compute distance between two claim chains. Treats null and empty as
    /// equivalent (distance 0).
    /// </summary>
    public static double Compute(IReadOnlyList<IdentityClaim>? a, IReadOnlyList<IdentityClaim>? b)
    {
        var ac = a?.Count ?? 0;
        var bc = b?.Count ?? 0;
        if (ac == 0 && bc == 0) return 0.0;

        var n = ac >= bc ? ac : bc;
        var total = 0.0;
        var weight = 1.0;

        // Walk from the leaf (last index) backwards. Position index 0 = leaf,
        // 1 = parent, etc. The aligned indices into a/b are (count - 1 - pos);
        // when negative, that side is padding.
        for (var pos = 0; pos < n; pos++)
        {
            var aIdx = ac - 1 - pos;
            var bIdx = bc - 1 - pos;
            var aClaim = aIdx >= 0 ? a![aIdx] : null;
            var bClaim = bIdx >= 0 ? b![bIdx] : null;
            total += weight * PositionCost(aClaim, bClaim);
            weight *= PositionDecay;
        }

        return total;
    }

    private static double PositionCost(IdentityClaim? a, IdentityClaim? b)
    {
        // Padding case: one side is missing a claim at this depth. A longer
        // chain doesn't get "free" distance from extra ancestors — it pays
        // the padding cost.
        if (a is null || b is null) return PaddingPenalty;

        // Tag mismatch short-circuits other comparisons at this position:
        // different tag = different element, no point summing class deltas.
        if (a.TagHash != b.TagHash) return TagMismatchPenalty;

        // Id is dispositive when both sides agree. An element identified by
        // <main id="content"> is the same element regardless of class drift.
        if (a.IdHash is ulong aid && b.IdHash is ulong bid)
        {
            if (aid == bid) return 0.0;
            return IdMismatchPenalty;
        }

        var cost = 0.0;

        // One-sided Id: the side with Id is more specific. Half-weight penalty.
        if (a.IdHash is not null || b.IdHash is not null)
            cost += OneSidedSpecificityPenalty;

        // Class symmetric difference, weighted by 1 / (combinedSetSize + 1) so
        // large class lists don't dominate. Combined size = unique-on-a +
        // unique-on-b + shared.
        cost += ClassCost(a.ClassHashes, b.ClassHashes);

        // data-* / aria-* attribute set differences.
        cost += AttrCost(a.DataAttrs, b.DataAttrs);
        cost += AttrCost(a.AriaAttrs, b.AriaAttrs);

        // role: equality after case-fold (extractor stores raw, but the
        // matcher compares case-insensitively, so mirror that here).
        cost += RoleCost(a.Role, b.Role);

        return cost;
    }

    private static double ClassCost(IReadOnlyList<ulong> a, IReadOnlyList<ulong> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0.0;

        // Count symmetric-difference elements (present on exactly one side).
        // Linear scans — class lists are typically <10 entries; HashSet would
        // out-allocate the savings.
        var symDiff = 0;
        var shared = 0;
        for (var i = 0; i < a.Count; i++)
        {
            if (Contains(b, a[i])) shared++;
            else symDiff++;
        }
        for (var i = 0; i < b.Count; i++)
        {
            if (!Contains(a, b[i])) symDiff++;
        }

        if (symDiff == 0) return 0.0;
        var combined = shared + symDiff;
        return symDiff / (double)(combined + 1);
    }

    private static bool Contains(IReadOnlyList<ulong> set, ulong value)
    {
        for (var i = 0; i < set.Count; i++)
            if (set[i] == value) return true;
        return false;
    }

    private static double AttrCost(
        IReadOnlyDictionary<string, string> a,
        IReadOnlyDictionary<string, string> b)
    {
        if (a.Count == 0 && b.Count == 0) return 0.0;

        var cost = 0.0;
        foreach (var kv in a)
        {
            if (b.TryGetValue(kv.Key, out var bv))
            {
                if (bv != kv.Value) cost += AttrMismatchPenalty;
            }
            else
            {
                // Present only on a — one-sided specificity.
                cost += OneSidedSpecificityPenalty;
            }
        }
        foreach (var kv in b)
        {
            if (!a.ContainsKey(kv.Key))
                cost += OneSidedSpecificityPenalty;
        }
        return cost;
    }

    private static double RoleCost(string? a, string? b)
    {
        if (a is null && b is null) return 0.0;
        if (a is null || b is null) return OneSidedSpecificityPenalty;
        return string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            ? 0.0
            : AttrMismatchPenalty;
    }
}
