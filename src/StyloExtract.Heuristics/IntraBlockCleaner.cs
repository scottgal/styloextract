using System.Text.Json;
using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

/// <summary>
/// Strips nav/toc/toolbar/breadcrumb descendants that have been selected as part of
/// a larger content block. After scoring picks e.g. &lt;main&gt; as the highest-quality
/// subtree, internal toolbar, TOC, and breadcrumb descendants are still part of its
/// text content. This cleaner runs a second pass inside each selected content-role
/// block and removes those contaminating descendants in place.
///
/// The parsed IDocument is mutated; the extraction pipeline owns the document for the
/// duration of the call and never re-uses it, so mutation is safe.
/// </summary>
internal static class IntraBlockCleaner
{
    // Contamination hints loaded from the embedded JSON resource. The data lives in
    // Definitions/intra-block-contamination-hints.json so that adding patterns does not
    // require recompiling code. The list categories: standard nav/toc/toolbar/breadcrumb,
    // CMS-specific (MediaWiki, MS Docs, GitHub, dev portals), AI widgets and metadata
    // bars, plus e-commerce widget patterns (cart/wishlist/related-products/quick-shop/
    // newsletter/recommendations etc.) added in v1.5.4 after WCXB diagnostic showed 55
    // product pages emitting 30-90x too much content from those widgets.
    private static readonly HashSet<string> ContaminationHints = LoadContaminationHints();

    private static HashSet<string> LoadContaminationHints()
    {
        var assembly = typeof(IntraBlockCleaner).Assembly;
        var resName = assembly.GetManifestResourceNames()
            .Single(n => n.EndsWith("intra-block-contamination-hints.json", StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resName)!;
        var dto = JsonSerializer.Deserialize(stream, HeuristicsJsonContext.Default.HintList)!;
        return new HashSet<string>(dto.Hints, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Mutates the given content-role element by removing nav/toc/toolbar descendants
    /// and then collapsing any now-empty wrapper elements.
    /// </summary>
    public static void Clean(IElement contentElement)
    {
        // Pass 1: collect all contaminating descendants in a single traversal, then remove.
        // We collect first to avoid mutating the tree while iterating it.
        var toRemove = new List<IElement>();
        foreach (var descendant in DescendantsExcludingSelf(contentElement))
        {
            if (LooksLikeContamination(descendant))
                toRemove.Add(descendant);
        }

        // Remove collected nodes. Note: if a parent was marked and its child was also marked,
        // removing the parent is sufficient. We remove all collected items; AngleSharp handles
        // already-detached nodes gracefully (Remove() on a detached node is a no-op).
        foreach (var node in toRemove)
            node.Remove();

        // Pass 2: recursively collapse empty wrappers left behind by the strip.
        // Repeat until no more empty wrappers exist.
        bool changed;
        do
        {
            changed = false;
            // Collect in a snapshot before mutating.
            var emptyWrappers = new List<IElement>();
            foreach (var el in DescendantsExcludingSelf(contentElement))
            {
                if (el.ChildElementCount == 0
                    && string.IsNullOrWhiteSpace(el.TextContent)
                    && el.QuerySelector("img, video, audio, picture, iframe, embed, object") is null)
                {
                    emptyWrappers.Add(el);
                }
            }
            foreach (var node in emptyWrappers)
            {
                node.Remove();
                changed = true;
            }
        }
        while (changed);
    }

    private static bool LooksLikeContamination(IElement el)
    {
        var tag = el.LocalName;

        // A) Any <nav> descendant is contamination.
        if (tag == "nav") return true;

        // A) id or class contains a known contamination hint.
        //
        // Substring match (so "sidebar" matches "right-sidebar" + "sidebar-wrap" etc.)
        // is intentional, but it false-positives on CSS modifier-style class names
        // that embed a hint word as a state qualifier — e.g. WordPress / SNOFlex's
        // `<div class="sno-story-page sno-story-sidebar-mode-single">` is THE article
        // body, not a sidebar; the class is reading "the sidebar mode is single", not
        // "this element is a sidebar". Without a content-guard, the article gets
        // stripped and the page emits 1 char of MainContent.
        //
        // Guard: only treat as contamination if the element looks chrome-shaped —
        // i.e. small overall (< 1000 chars) OR mostly links (>= 0.4 density). A
        // 15 KB low-link-density div is content regardless of class string.
        var id = el.GetAttribute("id") ?? "";
        var cls = el.GetAttribute("class") ?? "";
        var idCls = (id + " " + cls).ToLowerInvariant();
        if (ContaminationHints.Any(h => idCls.Contains(h)))
        {
            const int ContentSizeMin = 1000;
            const double ContentLinkDensityMax = 0.4;
            if (el.TextContent.Length < ContentSizeMin || ComputeLinkDensity(el) >= ContentLinkDensityMax)
                return true;
        }

        // B) High-link-density <div> or <aside>: these are navigation listings,
        // related-article widgets, or sidebar link clusters inside the content subtree.
        if ((tag == "div" || tag == "aside")
            && el.TextContent.Length >= 80
            && ComputeLinkDensity(el) >= 0.6)
        {
            return true;
        }

        // C) <form> with no meaningful text inputs (mobile-nav toggle, search-only buttons, etc.)
        if (tag == "form")
        {
            var meaningfulInputs = el.QuerySelectorAll("input, textarea")
                .Count(input => IsMeaningfulInput(input));
            if (meaningfulInputs == 0) return true;
        }

        // C) <div> whose direct children are >80% interactive elements (button/a).
        // This catches toolbar clusters that were not named with a contamination hint.
        // Two or more interactive children at 80%+ is enough to flag a toolbar cluster.
        if (tag == "div" && el.ChildElementCount >= 2)
        {
            var children = el.Children;
            var interactiveCount = 0;
            foreach (var child in children)
            {
                var childTag = child.LocalName;
                if (childTag == "button" || childTag == "a")
                    interactiveCount++;
            }
            if ((double)interactiveCount / el.ChildElementCount >= 0.8)
                return true;
        }

        return false;
    }

    private static bool IsMeaningfulInput(IElement input)
    {
        if (input.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase)) return true;
        var type = (input.GetAttribute("type") ?? "text").ToLowerInvariant();
        return type is "text" or "email" or "search" or "tel" or "url"
            or "number" or "password" or "date" or "datetime-local"
            or "month" or "week" or "time";
    }

    private static double ComputeLinkDensity(IElement el)
    {
        var totalText = el.TextContent.Length;
        if (totalText == 0) return 0;
        var linkText = el.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / totalText;
    }

    /// <summary>
    /// Iterates descendants of <paramref name="root"/> in a depth-first order, excluding the root itself.
    /// Uses a snapshot of children at each level to avoid issues with concurrent modification.
    /// </summary>
    private static IEnumerable<IElement> DescendantsExcludingSelf(IElement root)
    {
        var stack = new Stack<IElement>();
        // Push children in reverse order so that first child is processed first.
        foreach (var child in root.Children.Reverse())
            stack.Push(child);

        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children.Reverse())
                stack.Push(child);
        }
    }
}
