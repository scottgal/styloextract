using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Identity-claim apply path. Replaces the CSS-string evaluator with a direct
/// per-element claim-set probe — every candidate of the leaf claim's tag has
/// its <see cref="ElementClaimSet"/> built once and tested against the chain
/// bottom-up. The ancestor walk uses strict-parent semantics so the resulting
/// match is congruent with the chain shape the inducer constructs in
/// <see cref="IdentityClaimSelectorBuilder.BuildAncestorChain"/>.
///
/// <para><b>Filter symmetry.</b> The filter passed here must be the same
/// instance (or behaviourally identical) to the one the inducer used. Each
/// claim's class list is what the inducer's filter let through at emission
/// time; if the apply-time filter rejects more classes, the element's class
/// set will lack the anchor and the match silently fails. The matcher only
/// requires claim classes to be a subset of element classes — looser apply
/// filters are safe; stricter ones break things.</para>
/// </summary>
public static class IdentityClaimApplicator
{
    /// <summary>
    /// Evaluate <paramref name="chain"/> against <paramref name="document"/>
    /// and return every element that satisfies the leaf claim with each
    /// ancestor claim satisfied somewhere in the strict-parent walk above it.
    /// Empty chain returns empty.
    /// </summary>
    public static IReadOnlyList<IElement> Apply(
        IReadOnlyList<IdentityClaim> chain,
        IDocument document,
        IClassStabilityFilter filter)
    {
        if (chain.Count == 0) return Array.Empty<IElement>();

        var leaf = chain[chain.Count - 1];
        var matches = new List<IElement>();

        foreach (var candidate in document.GetElementsByTagName(leaf.Tag))
        {
            var leafSet = IdentityClaimExtractor.Extract(candidate, filter);
            if (!IdentityClaimMatcher.Matches(leafSet, leaf)) continue;
            if (!ChainAncestorsMatch(candidate, chain, filter)) continue;
            matches.Add(candidate);
        }
        return matches;
    }

    /// <summary>
    /// Walk strict parents above <paramref name="leafElement"/>, satisfying
    /// each ancestor claim in order (innermost claim — chain[N-2] — must match
    /// the leaf's direct parent; outermost claim — chain[0] — must match the
    /// topmost ancestor in the consumed walk). Mirrors the chain construction
    /// in <see cref="IdentityClaimSelectorBuilder.ChainAncestorsMatch"/> so
    /// induction and apply agree on semantics.
    /// </summary>
    private static bool ChainAncestorsMatch(
        IElement leafElement,
        IReadOnlyList<IdentityClaim> chain,
        IClassStabilityFilter filter)
    {
        if (chain.Count <= 1) return true;

        var parent = leafElement.ParentElement;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            if (parent is null) return false;
            var set = IdentityClaimExtractor.Extract(parent, filter);
            if (!IdentityClaimMatcher.Matches(set, chain[i])) return false;
            parent = parent.ParentElement;
        }
        return true;
    }
}
