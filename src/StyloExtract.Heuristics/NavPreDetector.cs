using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Detects nav containers (PrimaryNavigation / SecondaryNavigation / Breadcrumb)
/// in real-world server-rendered HTML, ahead of the per-element classifier.
///
/// Background: HeuristicBlockClassifier classifies one element at a time and the
/// greedy non-overlapping selector picks the highest-scoring disjoint subset.
/// That works for content but loses nav signal in three common shapes:
///
///   1. <header><nav> wrapped in extra <div>s. The classifier rule `tag == "nav"`
///      fires, but deep descendants outscore the <nav> on text length and win
///      greedy. The <nav> ends up rejected as ancestor of an accepted descendant.
///   2. <header><ul> of <li>-of-links with no <nav> tag at all (mostlylucid.net,
///      many marketing sites). The <ul> is not in the segmenter set; descendant
///      <div> wrappers fall through to Boilerplate, producing deep-noise output.
///   3. <nav aria-label='breadcrumb'> or <ol class='breadcrumb'> — pre-fix,
///      these classified as PrimaryNavigation (any nav) or Boilerplate (the <ol>),
///      losing the Breadcrumb distinction the Sitemap profile needs.
///
/// The detector walks <header>, <footer>, and the document top looking for nav
/// containers, then returns one <see cref="NavGroup"/> per container. The caller
/// injects each as a high-score candidate AND demotes any descendants so greedy
/// selection picks the nav parent and stops descending into its noise.
/// </summary>
internal static class NavPreDetector
{
    /// <summary>Minimum &lt;li&gt; children for a header/footer &lt;ul&gt; to count as a link list.</summary>
    private const int MinUlListItems = 3;
    /// <summary>Minimum number of &lt;li&gt; children that must contain at least one &lt;a&gt;.</summary>
    private const int MinLinkBearingLi = 3;
    /// <summary>Minimum fraction of &lt;li&gt; children that must contain at least one &lt;a&gt;.</summary>
    /// <remarks>
    /// Relaxed from a stricter 0.8 — real-world header navs commonly carry non-link
    /// li's (logo, search box, theme toggle, login button). The mostlylucid.net header
    /// is logo + search + 4 link items + dark-mode toggle + login: 6/10 links = 0.6.
    /// Keeping 0.5 lets these nav-ul shapes register while still filtering out content
    /// li-lists like FAQ accordions (which usually have 0-1 link per li).
    /// </remarks>
    private const double LinkBearingLiRatio = 0.5;

    public static IReadOnlyList<NavGroup> Detect(IElement body)
    {
        var groups = new List<NavGroup>();
        var visited = new HashSet<IElement>(ReferenceEqualityComparer.Instance);

        // Skip-injection guard: nav containers that live INSIDE <main>/<article> are
        // intra-block contaminants (TOC widgets, in-article breadcrumbs, action bars).
        // IntraBlockCleaner already strips these post-selection so the surrounding
        // article still wins and emits a clean block. Hoisting them via injection
        // would steal the article's win and reduce MainContent to residue. Cache the
        // set so the per-rule helpers can ask cheaply.
        var contentRoots = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
        foreach (var el in DescendantsOf(body))
        {
            if (el.LocalName is "main" or "article") contentRoots.Add(el);
        }

        bool IsInsideContent(IElement el) => HasAncestorInSet(el, contentRoots);

        // Rule 4 (priority — breadcrumb specialisation must run before generic nav rules):
        // <nav aria-label="breadcrumb..."> | class~="breadcrumb"
        // <ol/<ul class~="breadcrumb">
        // Breadcrumbs inside <main>/<article> are intra-article contaminants —
        // IntraBlockCleaner already strips them; don't hoist or the main block loses.
        foreach (var el in DescendantsOf(body))
        {
            if (visited.Contains(el)) continue;
            if (IsInsideContent(el)) continue;
            if (IsBreadcrumbContainer(el))
            {
                groups.Add(new NavGroup(el, BlockRole.Breadcrumb, 0.95));
                MarkVisitedSubtree(el, visited);
            }
        }

        // Rule 5: <* role="navigation">  (PrimaryNavigation, 0.95)
        // Highest-confidence explicit declaration; runs before tag-based rules so
        // a div[role=navigation] wins over generic boilerplate fallthrough.
        foreach (var el in DescendantsOf(body))
        {
            if (visited.Contains(el)) continue;
            if (IsInsideContent(el)) continue;
            var role = el.GetAttribute("role");
            if (role != null && role.Equals("navigation", StringComparison.OrdinalIgnoreCase))
            {
                groups.Add(new NavGroup(el, BlockRole.PrimaryNavigation, 0.95));
                MarkVisitedSubtree(el, visited);
            }
        }

        // Rule 1: <header> ... <nav> (any depth inside the header)
        // Strongest signal short of role=navigation: an author-declared nav inside
        // an author-declared header.
        foreach (var header in DescendantsOf(body).Where(e => e.LocalName == "header"))
        {
            // Header *inside* main/article (page-title block in MS Docs / GitHub README
            // / etc.) is intra-content; its descendants are not chrome.
            if (IsInsideContent(header)) continue;
            foreach (var nav in DescendantsOf(header).Where(e => e.LocalName == "nav"))
            {
                if (visited.Contains(nav)) continue;
                groups.Add(new NavGroup(nav, BlockRole.PrimaryNavigation, 0.9));
                MarkVisitedSubtree(nav, visited);
            }
        }

        // Rule 3: <footer> ... <nav>  (SecondaryNavigation, 0.9)
        foreach (var footer in DescendantsOf(body).Where(e => e.LocalName == "footer"))
        {
            if (IsInsideContent(footer)) continue;
            foreach (var nav in DescendantsOf(footer).Where(e => e.LocalName == "nav"))
            {
                if (visited.Contains(nav)) continue;
                groups.Add(new NavGroup(nav, BlockRole.SecondaryNavigation, 0.9));
                MarkVisitedSubtree(nav, visited);
            }
        }

        // Rule 2: Top-of-document <nav>. Authors regularly drop a free-standing
        // <nav> at the top of <body> outside any <header> (e.g. Bootstrap navbars).
        // Treat any <nav> that appears in the first ~10% of body's element-preorder
        // and isn't already classified by rules 1/3/4/5 as PrimaryNavigation.
        var topOfDocCutoff = ComputeTopOfDocCutoff(body);
        int preOrder = 0;
        foreach (var el in DescendantsOf(body))
        {
            preOrder++;
            if (el.LocalName != "nav") continue;
            if (visited.Contains(el)) continue;
            if (IsInsideContent(el)) continue;
            if (preOrder > topOfDocCutoff) break;
            groups.Add(new NavGroup(el, BlockRole.PrimaryNavigation, 0.85));
            MarkVisitedSubtree(el, visited);
        }

        // Rule 6: Header <ul> of mostly-link <li>s.  Catches the mostlylucid pattern
        // (header > ul > li > a > div) where there is no <nav> tag at all and the
        // descendant wrappers would otherwise emit as deep Boilerplate.
        foreach (var header in DescendantsOf(body).Where(e => e.LocalName == "header"))
        {
            if (visited.Contains(header)) continue;
            if (IsInsideContent(header)) continue;
            foreach (var ul in DescendantsOf(header).Where(e => e.LocalName == "ul" || e.LocalName == "ol"))
            {
                if (visited.Contains(ul)) continue;
                if (IsLinkListUl(ul))
                {
                    groups.Add(new NavGroup(ul, BlockRole.PrimaryNavigation, 0.85));
                    MarkVisitedSubtree(ul, visited);
                }
            }
        }

        // Rule 7: Footer <ul> of mostly-link <li>s.
        foreach (var footer in DescendantsOf(body).Where(e => e.LocalName == "footer"))
        {
            if (visited.Contains(footer)) continue;
            if (IsInsideContent(footer)) continue;
            foreach (var ul in DescendantsOf(footer).Where(e => e.LocalName == "ul" || e.LocalName == "ol"))
            {
                if (visited.Contains(ul)) continue;
                if (IsLinkListUl(ul))
                {
                    groups.Add(new NavGroup(ul, BlockRole.SecondaryNavigation, 0.85));
                    MarkVisitedSubtree(ul, visited);
                }
            }
        }

        return groups;
    }

    private static bool HasAncestorInSet(IElement el, HashSet<IElement> set)
    {
        if (set.Count == 0) return false;
        var cur = el.ParentElement;
        while (cur is not null)
        {
            if (set.Contains(cur)) return true;
            cur = cur.ParentElement;
        }
        return false;
    }

    private static bool IsBreadcrumbContainer(IElement el)
    {
        var tag = el.LocalName;
        if (tag is not ("nav" or "ol" or "ul")) return false;

        // aria-label="breadcrumb" / "breadcrumbs"
        var aria = el.GetAttribute("aria-label");
        if (aria != null)
        {
            var ariaLower = aria.ToLowerInvariant();
            if (ariaLower.Contains("breadcrumb")) return true;
        }

        // class~="breadcrumb"
        var classAttr = el.GetAttribute("class");
        if (classAttr != null)
        {
            var classLower = classAttr.ToLowerInvariant();
            if (classLower.Contains("breadcrumb")) return true;
        }

        // id~="breadcrumb"
        var id = el.GetAttribute("id");
        if (id != null)
        {
            var idLower = id.ToLowerInvariant();
            if (idLower.Contains("breadcrumb")) return true;
        }

        return false;
    }

    private static bool IsLinkListUl(IElement ul)
    {
        // Count direct <li> children and how many contain at least one <a>.
        // A <ul> nested inside another already-detected nav <ul> is its dropdown —
        // visited-set already filters those out at the call site.
        int liCount = 0;
        int liWithLink = 0;
        foreach (var child in ul.Children)
        {
            if (!child.LocalName.Equals("li", StringComparison.OrdinalIgnoreCase)) continue;
            liCount++;
            if (child.QuerySelector("a") is not null) liWithLink++;
        }
        if (liCount < MinUlListItems) return false;
        if (liWithLink < MinLinkBearingLi) return false;
        var ratio = (double)liWithLink / liCount;
        return ratio >= LinkBearingLiRatio;
    }

    private static int ComputeTopOfDocCutoff(IElement body)
    {
        // Count all descendant elements; first 10% counts as "top of document".
        // Cheap one-pass walk; on a 2000-element page this is ~200 elements,
        // big enough to include header chrome but small enough to exclude the
        // article body and footer.
        int total = 0;
        foreach (var _ in DescendantsOf(body)) total++;
        return Math.Max(50, total / 10);
    }

    // Pre-order traversal, iterative to avoid recursion on Wikipedia-shaped trees.
    // Snapshots `IHtmlCollection<IElement>` to a plain array because AngleSharp 1.4.x
    // removed the int indexer on IHtmlCollection<T> (only the string indexer remains).
    // Snapshotting via ToArray keeps this compatible with both 1.3.0 and 1.4.x.
    private static IEnumerable<IElement> DescendantsOf(IElement root)
    {
        var stack = new Stack<IElement>();
        // Push children in reverse so iteration emits them in document order.
        var rootChildren = root.Children.ToArray();
        for (int i = rootChildren.Length - 1; i >= 0; i--)
        {
            stack.Push(rootChildren[i]);
        }
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            yield return cur;
            var curChildren = cur.Children.ToArray();
            for (int i = curChildren.Length - 1; i >= 0; i--)
            {
                stack.Push(curChildren[i]);
            }
        }
    }

    private static void MarkVisitedSubtree(IElement root, HashSet<IElement> visited)
    {
        visited.Add(root);
        foreach (var d in DescendantsOf(root))
        {
            visited.Add(d);
        }
    }
}

internal readonly record struct NavGroup(IElement Container, BlockRole Role, double Confidence);
