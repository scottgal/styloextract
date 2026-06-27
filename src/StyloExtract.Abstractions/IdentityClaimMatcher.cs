namespace StyloExtract.Abstractions;

public static class IdentityClaimMatcher
{
    /// <summary>
    /// Returns true if every non-null field of <paramref name="claim"/> is
    /// satisfied by <paramref name="element"/>. Comparison uses precomputed
    /// hashes where available to keep the hot path allocation-free.
    /// </summary>
    public static bool Matches(in ElementClaimSet element, IdentityClaim claim)
    {
        // Tag must match (always required).
        if (element.TagHash != claim.TagHash) return false;

        // Id (if claimed).
        if (claim.IdHash is ulong cid && element.IdHash != cid) return false;

        // All claimed classes must be present (order-insensitive).
        foreach (var c in claim.ClassHashes)
        {
            // Use a linear scan — class lists are typically <10 entries.
            // Switch to a HashSet only if profiling shows this hot.
            var found = false;
            foreach (var ec in element.ClassHashes)
                if (ec == c) { found = true; break; }
            if (!found) return false;
        }

        // data-* attrs must match by value.
        foreach (var kv in claim.DataAttrs)
        {
            if (!element.DataAttrs.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        }

        // aria-* attrs must match.
        foreach (var kv in claim.AriaAttrs)
        {
            if (!element.AriaAttrs.TryGetValue(kv.Key, out var v) || v != kv.Value)
                return false;
        }

        // role.
        if (claim.Role is not null)
        {
            if (element.Role is null) return false;
            if (!string.Equals(element.Role, claim.Role, StringComparison.OrdinalIgnoreCase))
                return false;
        }

        return true;
    }
}
