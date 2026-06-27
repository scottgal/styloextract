using System.IO.Hashing;
using System.Text;

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

    /// <summary>
    /// Hash-only overload for the streaming scanner's per-tag hot path.
    /// Compares <paramref name="element"/>'s precomputed hashes against
    /// <paramref name="claim"/>'s precomputed hashes without ever touching
    /// the string fields. Semantics match <see cref="Matches"/>:
    /// every non-null field of the claim must be satisfied by the element.
    ///
    /// data-* / aria-* matching hashes the claim's (name, value) strings
    /// once per call since the claim stores them as strings; this is fine
    /// because identity claims rarely carry data-/aria- requirements and
    /// the strings are short. If profiling shows it hot, precompute the
    /// pairs into the claim itself.
    /// </summary>
    public static bool MatchesByHash(in ElementHashSet element, IdentityClaim claim)
    {
        if (element.TagHash != claim.TagHash) return false;

        if (claim.IdHash is ulong cid && element.IdHash != cid) return false;

        foreach (var c in claim.ClassHashes)
        {
            var found = false;
            foreach (var ec in element.ClassHashes)
                if (ec == c) { found = true; break; }
            if (!found) return false;
        }

        if (claim.DataAttrs.Count > 0)
        {
            Span<byte> buf = stackalloc byte[256];
            foreach (var kv in claim.DataAttrs)
            {
                var nameHash = HashUtf8(kv.Key, buf);
                var valueHash = HashUtf8(kv.Value, buf);
                if (!HasAttrPair(element.DataAttrHashes, nameHash, valueHash))
                    return false;
            }
        }

        if (claim.AriaAttrs.Count > 0)
        {
            Span<byte> buf = stackalloc byte[256];
            foreach (var kv in claim.AriaAttrs)
            {
                var nameHash = HashUtf8(kv.Key, buf);
                var valueHash = HashUtf8(kv.Value, buf);
                if (!HasAttrPair(element.AriaAttrHashes, nameHash, valueHash))
                    return false;
            }
        }

        if (claim.Role is not null)
        {
            if (element.RoleHash == 0UL) return false;
            Span<byte> buf = stackalloc byte[64];
            var len = Encoding.UTF8.GetBytes(claim.Role.AsSpan(), buf);
            // Role compares case-insensitively in the string overload; hashes
            // are over the verbatim claim string. Stable as long as both
            // sides emit role in the same case (extractor + tokenizer both
            // pass raw attribute value through to XxHash3 without folding).
            // The extractor stores role verbatim, the tokenizer reads it
            // verbatim, so this stays consistent in practice.
            if (XxHash3.HashToUInt64(buf[..len]) != element.RoleHash) return false;
        }

        return true;
    }

    private static ulong HashUtf8(string s, Span<byte> buf)
    {
        var needed = Encoding.UTF8.GetByteCount(s);
        if (needed <= buf.Length)
        {
            var len = Encoding.UTF8.GetBytes(s.AsSpan(), buf);
            return XxHash3.HashToUInt64(buf[..len]);
        }
        // Fallback for unusually long attr names/values.
        var bytes = Encoding.UTF8.GetBytes(s);
        return XxHash3.HashToUInt64(bytes);
    }

    private static bool HasAttrPair(IReadOnlyList<AttrHashPair> pairs, ulong nameHash, ulong valueHash)
    {
        for (int i = 0; i < pairs.Count; i++)
        {
            var p = pairs[i];
            if (p.NameHash == nameHash && p.ValueHash == valueHash) return true;
        }
        return false;
    }
}
