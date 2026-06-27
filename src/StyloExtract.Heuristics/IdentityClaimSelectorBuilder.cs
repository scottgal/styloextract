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
