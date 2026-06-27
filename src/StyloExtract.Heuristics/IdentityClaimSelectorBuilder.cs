using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Builds an identity-claim ancestor chain that uniquely anchors a target
/// element within its document. Replaces the legacy
/// <see cref="XPathBuilder.ComputeXPath"/> + <see cref="CssSelectorGeneralizer.Generalize"/>
/// emit path in the heuristic inducer.
///
/// Algorithm:
/// <list type="number">
/// <item>Extract the target's <see cref="ElementClaimSet"/>.</item>
/// <item>Pick its best single claim via <see cref="SelectorPenaltyScorer"/>.
/// When multiple stable classes are present, the most-specific is chosen by
/// document frequency (fewest matches across the document).</item>
/// <item>Probe uniqueness across all tag-matching elements.</item>
/// <item>If not unique, walk up one ancestor, prepend its best claim,
/// re-probe.</item>
/// <item>Cap at <see cref="MaxChainDepth"/> ancestors. When the cap is hit,
/// return whatever chain was built and signal the caller via
/// <see cref="BuildResult.HitDepthCap"/>.</item>
/// </list>
///
/// <para><b>Hard uniqueness postcondition (Task 51 / 2.1).</b> If the
/// ancestor walk extends to the cap and the chain still matches more than
/// one element on the induction document, <see cref="BuildResult.HitDepthCap"/>
/// is set to true. The caller (the heuristic inducer) MUST then force the
/// rule's confidence to 0.0 — a chain that's not unique at induction will
/// pick arbitrary elements at apply time and produce wrong markdown.
/// </para>
/// </summary>
public static class IdentityClaimSelectorBuilder
{
    /// <summary>
    /// Maximum ancestors above the target the builder will prepend before
    /// giving up. Extended from 4 to 8 in Task 51 / 2.1: real-world pages
    /// (Tailwind-heavy SPAs, deeply nested utility-class soup) frequently
    /// need 5-7 ancestors to reach a unique anchor. The cost is bounded —
    /// each step does one tag-set scan + one ancestor walk per match.
    /// </summary>
    public const int MaxChainDepth = 8;

    public readonly record struct BuildResult(IdentityClaim[] Chain, bool HitDepthCap);

    public static BuildResult BuildAncestorChain(
        IElement target,
        IDocument document,
        IClassStabilityFilter filter) =>
        BuildAncestorChain(target, document, filter, logger: null);

    /// <summary>
    /// Logger overload — emits a debug-level event when the walk falls back
    /// to a deep chain (length &gt; 4) and a warning when the cap is hit
    /// without uniqueness. Used by the inducer; tests typically call the
    /// no-logger overload.
    /// </summary>
    public static BuildResult BuildAncestorChain(
        IElement target,
        IDocument document,
        IClassStabilityFilter filter,
        ILogger? logger)
    {
        var chain = new List<IdentityClaim>(MaxChainDepth + 1);
        var elements = new List<IElement>(MaxChainDepth + 1);

        // Build the target's claim first.
        elements.Add(target);
        chain.Add(BuildBestClaim(target, document, filter));

        if (IsUnique(chain, document)) return new BuildResult(chain.ToArray(), HitDepthCap: false);

        // Walk ancestors. Cap at MaxChainDepth ancestors above the target.
        var current = target.ParentElement;
        var depth = 0;
        while (current is not null && depth < MaxChainDepth)
        {
            // Walk has to stop at the document element. AngleSharp's ParentElement
            // returns null when we hit <html>'s parent (the document), so the
            // natural termination is the loop condition above.
            elements.Insert(0, current);
            chain.Insert(0, BuildBestClaim(current, document, filter));

            if (IsUnique(chain, document))
            {
                if (chain.Count > 4)
                {
                    logger?.LogDebug(
                        "IdentityClaim chain reached uniqueness at depth {Depth} (chain length {Length}); deep walks indicate utility-class-heavy markup or low identity density.",
                        depth + 1, chain.Count);
                }
                return new BuildResult(chain.ToArray(), HitDepthCap: false);
            }

            current = current.ParentElement;
            depth++;
        }

        logger?.LogWarning(
            "IdentityClaim chain hit MaxChainDepth ({MaxDepth}) without uniqueness — emitting non-unique chain with requires-review marker. Target tag: {Tag}",
            MaxChainDepth, target.LocalName);

        return new BuildResult(chain.ToArray(), HitDepthCap: true);
    }

    // -----------------------------------------------------------------------
    // Task 52: cardinality-aware uniqueness for repeated roles.
    //
    // A repeated role (RepeatedItem, Article-as-list, etc.) is represented by
    // K distinct target elements. The emitted selector must match exactly that
    // set of K — not 1, not K+M, not K-1. The single-target BuildAncestorChain
    // overload cannot express that shape because its uniqueness probe gives up
    // the moment it sees more than one match.
    // -----------------------------------------------------------------------

    public static BuildResult BuildForRepeatedRole(
        IReadOnlyList<IElement> targets,
        IDocument document,
        IClassStabilityFilter filter) =>
        BuildForRepeatedRole(targets, document, filter, expectedCardinality: targets.Count, logger: null);

    public static BuildResult BuildForRepeatedRole(
        IReadOnlyList<IElement> targets,
        IDocument document,
        IClassStabilityFilter filter,
        ILogger? logger) =>
        BuildForRepeatedRole(targets, document, filter, expectedCardinality: targets.Count, logger);

    /// <summary>
    /// Build a claim chain that matches exactly <paramref name="expectedCardinality"/>
    /// elements on the induction document — the set of <paramref name="targets"/>.
    ///
    /// <para><b>Algorithm.</b></para>
    /// <list type="number">
    /// <item>Compute the intersection of every target's <see cref="ElementClaimSet"/>
    ///   (shared classes, shared data-/aria-* pairs, shared role). All targets
    ///   must agree on tag — if they don't, the caller's role-classification is
    ///   inconsistent and the builder returns an empty chain with
    ///   <see cref="BuildResult.HitDepthCap"/>=true.</item>
    /// <item>Build a candidate claim from the intersection and probe the
    ///   document. Equal cardinality + matched set == target set → done.</item>
    /// <item>Overmatch (count &gt; K): walk up to the targets' lowest common
    ///   ancestor and prepend its identity claim as a scope prefix; re-probe.
    ///   Cap at <see cref="MaxChainDepth"/> ancestor steps.</item>
    /// <item>Undermatch (count &lt; K): the intersection over-specified — drop
    ///   the most restrictive shared element (a class first, then attrs) and
    ///   re-probe.</item>
    /// </list>
    ///
    /// <para><b>Postcondition.</b> When neither path reaches the exact target
    /// set within the cap, <see cref="BuildResult.HitDepthCap"/> is set so the
    /// inducer can force <c>confidence = 0.0</c> as a requires-review marker
    /// (same convention as the single-target overload).</para>
    /// </summary>
    public static BuildResult BuildForRepeatedRole(
        IReadOnlyList<IElement> targets,
        IDocument document,
        IClassStabilityFilter filter,
        int expectedCardinality,
        ILogger? logger)
    {
        if (targets.Count == 0)
        {
            return new BuildResult(Array.Empty<IdentityClaim>(), HitDepthCap: true);
        }

        // Degenerate: a "repeated" role with one target reduces to the
        // single-target case.
        if (targets.Count == 1)
        {
            return BuildAncestorChain(targets[0], document, filter, logger);
        }

        // Tag agreement is a precondition. Heterogeneous tag-mix in a single
        // repeated-role set is a classifier bug — emit empty chain + cap marker.
        var firstTag = targets[0].LocalName.ToLowerInvariant();
        for (var i = 1; i < targets.Count; i++)
        {
            if (!string.Equals(targets[i].LocalName, firstTag, StringComparison.OrdinalIgnoreCase))
            {
                logger?.LogWarning(
                    "BuildForRepeatedRole: target tag mismatch ({Tag0} vs {TagI}) — heuristic classifier put incompatible elements in the same repeated role. Emitting empty chain.",
                    firstTag, targets[i].LocalName);
                return new BuildResult(Array.Empty<IdentityClaim>(), HitDepthCap: true);
            }
        }

        // Extract per-target claim sets once; reused by intersection + probe.
        var sets = new ElementClaimSet[targets.Count];
        for (var i = 0; i < targets.Count; i++)
        {
            sets[i] = IdentityClaimExtractor.Extract(targets[i], filter);
        }

        // Compute the intersection of shared identity attributes across all
        // targets. Ids are skipped (unique per element by definition); classes
        // are kept only if present on every target; attrs by (key, value) pair.
        var inter = ComputeIntersection(sets);

        // Probe the candidate leaf claim against the document. Build a chain
        // starting from the leaf, then try ancestor-scope narrowing if it
        // overmatches, attr-drop if it undermatches.
        var chain = new List<IdentityClaim>(MaxChainDepth + 1) { BuildClaimFromIntersection(firstTag, inter) };

        if (TryMatchExactSet(chain, document, targets, out _))
        {
            return new BuildResult(chain.ToArray(), HitDepthCap: false);
        }

        // Drop most-restrictive intersection elements while undermatching.
        // ProbeCount counts how many elements on the doc match the chain right
        // now; if it's less than expectedCardinality, the intersection is
        // over-specified. Try removing one class at a time (most-restrictive
        // first — measured by document frequency: keep the lowest-frequency
        // class, drop the higher-frequency ones).
        //
        // Then, if still undermatching, drop attrs.
        while (ProbeCount(chain, document) < expectedCardinality &&
               TryDropMostRestrictive(ref inter, document, firstTag))
        {
            chain[0] = BuildClaimFromIntersection(firstTag, inter);
            if (TryMatchExactSet(chain, document, targets, out _))
            {
                return new BuildResult(chain.ToArray(), HitDepthCap: false);
            }
        }

        // Overmatch path: narrow with the LCA's identity claim. Walk up from
        // the LCA one ancestor at a time, prepending each ancestor's claim
        // until the chain matches the target set exactly (or we hit the cap).
        var lca = FindLowestCommonAncestor(targets);
        var depth = 0;
        var ancestor = lca;
        while (ancestor is not null && depth < MaxChainDepth)
        {
            // Skip prepending the document element — it can't disambiguate
            // anything and pollutes the chain.
            if (ancestor.ParentElement is null) break;

            chain.Insert(0, BuildBestClaim(ancestor, document, filter));

            if (TryMatchExactSet(chain, document, targets, out _))
            {
                if (chain.Count > 4)
                {
                    logger?.LogDebug(
                        "Repeated-role chain reached cardinality {K} at depth {Depth} (chain length {Length}).",
                        expectedCardinality, depth + 1, chain.Count);
                }
                return new BuildResult(chain.ToArray(), HitDepthCap: false);
            }

            ancestor = ancestor.ParentElement;
            depth++;
        }

        logger?.LogWarning(
            "BuildForRepeatedRole hit MaxChainDepth ({MaxDepth}) without reaching cardinality {K}. Tag: {Tag}, targets: {TargetCount}.",
            MaxChainDepth, expectedCardinality, firstTag, targets.Count);

        return new BuildResult(chain.ToArray(), HitDepthCap: true);
    }

    /// <summary>
    /// Mutable bag describing the intersection of identity attributes across
    /// the K targets. Mutated by <see cref="TryDropMostRestrictive"/> when the
    /// undermatch branch needs to widen the candidate claim.
    /// </summary>
    private sealed class Intersection
    {
        public List<string> Classes { get; set; } = new();
        public Dictionary<string, string> DataAttrs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> AriaAttrs { get; set; } = new(StringComparer.OrdinalIgnoreCase);
        public string? Role { get; set; }
    }

    private static Intersection ComputeIntersection(ElementClaimSet[] sets)
    {
        var inter = new Intersection();

        // Classes: start from sets[0]'s classes, then intersect with each next
        // set's class hash set. Preserve sets[0]'s class order so the emitted
        // claim is deterministic across runs.
        if (sets.Length > 0)
        {
            foreach (var c in sets[0].Classes)
            {
                var inAll = true;
                for (var i = 1; i < sets.Length; i++)
                {
                    if (!sets[i].Classes.Contains(c))
                    {
                        inAll = false;
                        break;
                    }
                }
                if (inAll) inter.Classes.Add(c);
            }

            // data-* and aria-* by (key, value) pair.
            foreach (var kv in sets[0].DataAttrs)
            {
                var inAll = true;
                for (var i = 1; i < sets.Length; i++)
                {
                    if (!sets[i].DataAttrs.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    {
                        inAll = false;
                        break;
                    }
                }
                if (inAll) inter.DataAttrs[kv.Key] = kv.Value;
            }
            foreach (var kv in sets[0].AriaAttrs)
            {
                var inAll = true;
                for (var i = 1; i < sets.Length; i++)
                {
                    if (!sets[i].AriaAttrs.TryGetValue(kv.Key, out var v) || v != kv.Value)
                    {
                        inAll = false;
                        break;
                    }
                }
                if (inAll) inter.AriaAttrs[kv.Key] = kv.Value;
            }

            // role.
            if (sets[0].Role is { } role0)
            {
                var inAll = true;
                for (var i = 1; i < sets.Length; i++)
                {
                    if (!string.Equals(sets[i].Role, role0, StringComparison.OrdinalIgnoreCase))
                    {
                        inAll = false;
                        break;
                    }
                }
                if (inAll) inter.Role = role0;
            }
        }

        return inter;
    }

    private static IdentityClaim BuildClaimFromIntersection(string tag, Intersection inter)
    {
        var tagHash = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(tag));
        var classHashes = new ulong[inter.Classes.Count];
        for (var i = 0; i < inter.Classes.Count; i++)
        {
            classHashes[i] = XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(inter.Classes[i]));
        }
        return new IdentityClaim
        {
            Tag = tag,
            TagHash = tagHash,
            Classes = inter.Classes.ToArray(),
            ClassHashes = classHashes,
            DataAttrs = new Dictionary<string, string>(inter.DataAttrs, StringComparer.OrdinalIgnoreCase),
            AriaAttrs = new Dictionary<string, string>(inter.AriaAttrs, StringComparer.OrdinalIgnoreCase),
            Role = inter.Role,
        };
    }

    /// <summary>
    /// Drop the most-restrictive element from the intersection so the claim
    /// widens. Restrictive = lowest document frequency: a class that appears
    /// on few elements rejects more targets than a common one. Drop attrs only
    /// after all classes are exhausted (attrs are usually highly restrictive
    /// and rarely shared across many targets anyway). Returns false when the
    /// intersection is already empty — no further widening is possible.
    /// </summary>
    private static bool TryDropMostRestrictive(ref Intersection inter, IDocument document, string tag)
    {
        if (inter.Classes.Count > 0)
        {
            // Pick the class with the LOWEST document frequency — that's the
            // one most likely to be excluding legitimate targets.
            var dropIdx = 0;
            var dropCount = int.MaxValue;
            for (var i = 0; i < inter.Classes.Count; i++)
            {
                int count;
                try
                {
                    count = document.QuerySelectorAll($"{tag}.{CssEscapeIdent(inter.Classes[i])}").Length;
                }
                catch
                {
                    count = int.MaxValue;
                }
                if (count < dropCount)
                {
                    dropCount = count;
                    dropIdx = i;
                }
            }
            inter.Classes.RemoveAt(dropIdx);
            return true;
        }
        if (inter.DataAttrs.Count > 0)
        {
            // Pop deterministically — first by ordinal name.
            var key = inter.DataAttrs.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            inter.DataAttrs.Remove(key);
            return true;
        }
        if (inter.AriaAttrs.Count > 0)
        {
            var key = inter.AriaAttrs.Keys.OrderBy(k => k, StringComparer.Ordinal).First();
            inter.AriaAttrs.Remove(key);
            return true;
        }
        if (inter.Role is not null)
        {
            inter.Role = null;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Probe a chain against the document and return true iff the matched
    /// element set is exactly <paramref name="targets"/> (same elements, no
    /// extras, no missing). The matched set is emitted via the out parameter
    /// so callers can decide between overmatch and undermatch fallbacks.
    /// </summary>
    private static bool TryMatchExactSet(
        IReadOnlyList<IdentityClaim> chain,
        IDocument document,
        IReadOnlyList<IElement> targets,
        out List<IElement> matched)
    {
        matched = ProbeMatches(chain, document);
        if (matched.Count != targets.Count) return false;
        // Set equality by reference — DOM elements are reference-stable.
        foreach (var t in targets)
        {
            var found = false;
            foreach (var m in matched)
            {
                if (ReferenceEquals(m, t)) { found = true; break; }
            }
            if (!found) return false;
        }
        return true;
    }

    private static int ProbeCount(IReadOnlyList<IdentityClaim> chain, IDocument document) =>
        ProbeMatches(chain, document).Count;

    private static List<IElement> ProbeMatches(IReadOnlyList<IdentityClaim> chain, IDocument document)
    {
        var results = new List<IElement>();
        if (chain.Count == 0) return results;
        var leaf = chain[^1];

        foreach (var el in document.GetElementsByTagName(leaf.Tag))
        {
            var set = IdentityClaimExtractor.Extract(el, NullClassFilter.Instance);
            if (!IdentityClaimMatcher.Matches(set, leaf)) continue;
            if (!ChainAncestorsMatch(el, chain)) continue;
            results.Add(el);
        }
        return results;
    }

    /// <summary>
    /// Lowest common ancestor of a set of DOM elements. Walks each target up
    /// to the document root, then intersects the ancestor sets. Returns null
    /// only if the targets live in unrelated subtrees (shouldn't happen for
    /// elements from the same document).
    /// </summary>
    private static IElement? FindLowestCommonAncestor(IReadOnlyList<IElement> targets)
    {
        if (targets.Count == 0) return null;
        if (targets.Count == 1) return targets[0].ParentElement;

        // Collect target[0]'s ancestor path (root to itself).
        var path0 = new List<IElement>();
        for (var e = targets[0]; e is not null; e = e.ParentElement)
        {
            path0.Add(e);
        }
        path0.Reverse(); // root first

        // For each other target, mark the longest path0 prefix that's also one
        // of its ancestors. The shortest surviving prefix length across all
        // targets is the LCA depth.
        var commonLen = path0.Count;
        for (var i = 1; i < targets.Count; i++)
        {
            var t = targets[i];
            // Build path for t.
            var pathT = new List<IElement>();
            for (var e = t; e is not null; e = e.ParentElement) pathT.Add(e);
            pathT.Reverse();

            var max = Math.Min(commonLen, pathT.Count);
            var k = 0;
            while (k < max && ReferenceEquals(path0[k], pathT[k])) k++;
            commonLen = k;
            if (commonLen == 0) return null;
        }

        // commonLen-1 is the index of the deepest shared ancestor.
        return commonLen > 0 ? path0[commonLen - 1] : null;
    }

    /// <summary>
    /// Render an identity-claim chain to a CSS selector string for the
    /// back-compat <see cref="BlockRule.CssSelectors"/> field. Each claim becomes
    /// one CSS step, joined with " &gt; " to express the strict-parent chain
    /// the matcher actually enforces.
    /// </summary>
    public static string ToCssSelector(IReadOnlyList<IdentityClaim> chain)
    {
        if (chain.Count == 0) return "";
        var parts = new string[chain.Count];
        for (var i = 0; i < chain.Count; i++) parts[i] = ToCssStep(chain[i]);
        return string.Join(" > ", parts);
    }

    private static string ToCssStep(IdentityClaim claim)
    {
        var step = claim.Tag;
        if (claim.Id is { } id) step += $"#{CssEscapeIdent(id)}";
        foreach (var c in claim.Classes) step += $".{CssEscapeIdent(c)}";
        foreach (var kv in claim.DataAttrs) step += $"[data-{kv.Key}=\"{CssEscapeAttrValue(kv.Value)}\"]";
        foreach (var kv in claim.AriaAttrs) step += $"[aria-{kv.Key}=\"{CssEscapeAttrValue(kv.Value)}\"]";
        if (claim.Role is { } role) step += $"[role=\"{CssEscapeAttrValue(role)}\"]";
        return step;
    }

    private static IdentityClaim BuildBestClaim(IElement element, IDocument document, IClassStabilityFilter filter)
    {
        var set = IdentityClaimExtractor.Extract(element, filter);
        string? bestClass = null;
        if (set.Classes.Count > 1)
        {
            bestClass = PickMostSpecificClass(set.Classes, document);
        }
        return SelectorPenaltyScorer.PickBest(set, bestClass).Claim;
    }

    private static string PickMostSpecificClass(IReadOnlyList<string> candidates, IDocument document)
    {
        // Most-specific = lowest document frequency. Tie-broken by first appearance.
        var best = candidates[0];
        var bestCount = int.MaxValue;
        foreach (var c in candidates)
        {
            int count;
            try
            {
                // Class names that passed the stability filter are safe to slot
                // into a CSS selector unescaped; the filter rejects empty, leading-
                // digit, and hash-shaped tokens. Defensive try/catch covers the
                // exotic-but-stable case (e.g. "post--featured" with double dash).
                count = document.QuerySelectorAll($".{CssEscapeIdent(c)}").Length;
            }
            catch
            {
                count = int.MaxValue;
            }
            if (count < bestCount)
            {
                bestCount = count;
                best = c;
            }
        }
        return best;
    }

    private static bool IsUnique(IReadOnlyList<IdentityClaim> chain, IDocument document)
    {
        if (chain.Count == 0) return false;
        var leaf = chain[chain.Count - 1];

        // Candidate set: every element whose local tag matches the leaf claim's
        // tag. Walking by tag keeps the inner loop linear in the document's
        // count of that tag, which is what we want for cheap uniqueness probes.
        var candidates = document.GetElementsByTagName(leaf.Tag);
        var matches = 0;
        foreach (var el in candidates)
        {
            var set = IdentityClaimExtractor.Extract(el, NullClassFilter.Instance);
            if (!IdentityClaimMatcher.Matches(set, leaf)) continue;
            if (!ChainAncestorsMatch(el, chain)) continue;

            matches++;
            if (matches > 1) return false; // can't be unique anymore
        }
        return matches == 1;
    }

    private static bool ChainAncestorsMatch(IElement leafElement, IReadOnlyList<IdentityClaim> chain)
    {
        // chain[N-1] already matched the leaf. The builder constructs chains by
        // walking strict parent links one at a time, so each chain[i] must match
        // the immediate parent of the element that satisfied chain[i+1]. This
        // matches the CSS rendering using the '>' child combinator.
        if (chain.Count <= 1) return true;

        var parent = leafElement.ParentElement;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            if (parent is null) return false;
            var set = IdentityClaimExtractor.Extract(parent, NullClassFilter.Instance);
            if (!IdentityClaimMatcher.Matches(set, chain[i])) return false;
            parent = parent.ParentElement;
        }
        return true;
    }

    private static string CssEscapeIdent(string s)
    {
        // Minimal CSS-identifier escape: backslash-prefix anything outside
        // [A-Za-z0-9_-] and a leading digit. The stability filter rejects the
        // worst offenders, so this is just defence-in-depth.
        if (string.IsNullOrEmpty(s)) return s;
        var sb = new System.Text.StringBuilder(s.Length);
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            var isWordChar = char.IsLetterOrDigit(c) || c == '_' || c == '-';
            if (i == 0 && char.IsDigit(c)) sb.Append('\\');
            if (!isWordChar) sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string CssEscapeAttrValue(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>
    /// Pass-through filter used when re-extracting candidate elements during
    /// uniqueness probes. The claim being matched already encodes a stability-
    /// filtered class list, so re-filtering at probe time would double-reject.
    /// </summary>
    private sealed class NullClassFilter : IClassStabilityFilter
    {
        public static readonly NullClassFilter Instance = new();
        public bool IsStable(string token) => true;
    }
}
