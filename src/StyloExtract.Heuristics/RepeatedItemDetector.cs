using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

internal record RepeatedItemGroup(IElement Container, IReadOnlyList<IElement> Items);

/// <summary>
/// Detects containers whose direct children form a homogeneous repeated-block pattern:
/// forum posts, listing cards, collection items, product tiles. When found, the caller
/// promotes each child to a <see cref="StyloExtract.Abstractions.BlockRole.RepeatedItem"/>
/// block instead of letting the greedy selector pick a single best child.
///
/// Guards against false positives on navigation menus, table rows, and structural
/// chrome containers (header, footer, nav, aside) by skipping those container tags
/// and requiring substantial per-child text.
/// </summary>
internal static class RepeatedItemDetector
{
    private const int MinChildren = 3;
    // Minimum trimmed text length per child (excludes whitespace-only children).
    // Using TrimmedLength prevents version-number lists like "\n  devel\n" (long due to
    // whitespace but short in actual content) from triggering the detector.
    private const int MinChildTextLength = 100;
    private const double MinClassOverlap = 0.5;

    // SVG text that exceeds this length indicates the element is primarily a diagram
    // container (railroad syntax charts, data-vis SVGs) rather than prose content.
    // Inline icon SVGs typically produce near-zero text; diagram SVGs produce hundreds
    // of keyword labels. Skip any child whose SVG descendants contribute more than this
    // many characters — their TextContent is dominated by diagram keyword noise, not prose.
    private const int SvgTextLengthThreshold = 100;

    // Container tags that should never be treated as repeated-item wrappers.
    // table/tbody/thead: rows are handled by the Table renderer.
    // header/footer/nav/aside: structural chrome.
    // head/html/body: document skeleton.
    // article/main/section: single-content semantic elements whose child paragraphs
    //   are prose flow, not repeated structural blocks.
    private static readonly HashSet<string> SkipContainerTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "table", "tbody", "thead", "tfoot", "tr",
        "head", "html", "body",
        "header", "footer", "nav", "aside",
        "article", "main", "section",
        // Definition-list containers: dd (definition description) and dt (term) hold
        // single-definition content, not collections of independent items.
        "dd", "dt", "li",
        // Inline/phrase elements that can nest block content in HTML5 but are not containers
        // for repeated structural items.
        "p", "span", "blockquote",
    };

    // Child element tags that are prose/inline-flow, list containers, or semantic
    // document structure elements and must not be treated as repeated structural items
    // even when they are numerous and share the same tag.
    // <p>: paragraphs inside an article body.
    // <ul>/<ol>/<dl>: list containers that are part of content flow.
    // <li>: list items (part of list structure, not structural repeated blocks).
    // <dt>/<dd>: definition list terms/descriptions.
    // <span>: inline spans.
    // <td>/<th>: table cells.
    // <blockquote>: prose quotations.
    // <h1>-<h6>: headings (structural document markers, not repeated content items).
    // <pre>/<code>: code blocks.
    // <section>: documentation and articles frequently use multiple <section> elements
    //   as subdivisions of a single topic (MDN, ReadTheDocs, etc.). Sections within a
    //   document are content flow, not independent repeated-item collections.
    private static readonly HashSet<string> SkipChildTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "p", "ul", "ol", "dl", "li", "dt", "dd", "span", "td", "th",
        "blockquote", "h1", "h2", "h3", "h4", "h5", "h6", "pre", "code",
        "section",
    };

    /// <summary>
    /// Walk the subtree rooted at <paramref name="root"/>, find non-overlapping
    /// repeated-item groups, and return them biggest-first.
    /// </summary>
    public static IReadOnlyList<RepeatedItemGroup> Detect(IElement root)
    {
        var groups = new List<RepeatedItemGroup>();

        foreach (var container in DescendantsIncludingSelf(root))
        {
            if (SkipContainerTags.Contains(container.TagName)) continue;

            // Skip containers that are inside structural chrome elements (article, footer,
            // header, nav, aside, form). An <article> represents a single self-contained
            // composition; structural sub-divs inside it are part of that article's internal
            // structure. Footer/header/nav chrome can contain repeated column divs that look
            // like items (site-map columns, nav dropdowns) but are navigation, not content.
            // <form> children are form fields (input rows, section headers), not content items.
            // Forum sites that use <article class="post"> per post are handled correctly because
            // those <article> elements are siblings inside a plain <div> container (not nested
            // inside a parent <article> or <footer>).
            if (HasSkipAncestor(container)) continue;

            var children = container.Children.ToList();
            if (children.Count < MinChildren) continue;

            // Filter children: must have substantial trimmed text (count non-whitespace
            // characters so that version-number lists like "\n  devel\n" don't qualify).
            // Also exclude children that are primarily SVG diagram containers: inline SVG
            // elements (syntax railroad diagrams, data-vis charts) produce substantial
            // TextContent from keyword labels, but that text is not prose content.
            // Skip any child whose SVG descendants emit more than SvgTextLengthThreshold
            // characters — they are diagram/visualisation wrappers, not repeating items.
            var substantialChildren = children
                .Where(c =>
                    c.TextContent.Trim().Length >= MinChildTextLength
                    && !HasSubstantialSvgContent(c))
                .ToList();

            if (substantialChildren.Count < MinChildren) continue;

            // Group by tag name; within each tag group require class-signature similarity.
            // Exclude prose/inline-flow child tags (p, li, etc.) — they are content flow
            // inside a single block, not structural repeated items.
            var byTag = substantialChildren
                .Where(c => !SkipChildTags.Contains(c.TagName.ToLowerInvariant()))
                .GroupBy(c => c.TagName.ToLowerInvariant());

            foreach (var tagGroup in byTag)
            {
                var items = tagGroup.ToList();
                if (items.Count < MinChildren) continue;
                if (!HaveSimilarClassSignatures(items)) continue;

                // Reject groups where items have high link density: these are navigation
                // listings (related questions, sponsored links, navigation columns).
                // Content items (forum posts, product cards, listing entries) have low-to-medium
                // link density; navigation lists are predominantly hyperlinks.
                var avgLinkDensity = items.Average(ComputeLinkDensity);
                if (avgLinkDensity > 0.65) continue;

                groups.Add(new RepeatedItemGroup(container, items));
            }
        }

        // Non-overlapping: prefer the biggest group. Drop groups whose container is
        // already consumed (descendant of an accepted container) or whose items
        // overlap with an already-accepted group's items.
        groups.Sort((a, b) => b.Items.Count.CompareTo(a.Items.Count));

        var selected = new List<RepeatedItemGroup>();
        var consumed = new HashSet<IElement>(ReferenceEqualityComparer.Instance);

        foreach (var g in groups)
        {
            // Skip if this container is inside an already-selected container.
            if (IsConsumed(g.Container, consumed)) continue;
            // Skip if any of this group's items are already consumed.
            if (g.Items.Any(i => IsConsumed(i, consumed))) continue;

            selected.Add(g);
            // Mark the container and all items + their descendants as consumed.
            consumed.Add(g.Container);
            foreach (var item in g.Items)
            {
                MarkConsumed(item, consumed);
            }
        }

        return selected;
    }

    // Ancestor element tags that indicate the container is inside structural chrome.
    // If a container is nested inside any of these, it is not a repeated-content region.
    // article: single-composition content (code examples, version notes are part of the article).
    // footer/header/nav/aside: structural page chrome (navigation columns, site maps, sponsor rows).
    // form: children of a <form> are form fields (input rows, section dividers, validation hints),
    //   not content items. Gravity Forms, WPForms and similar CMS form plugins produce 10-50
    //   repeated <div class="gfield ..."> siblings that all pass the text and class-overlap
    //   checks but are input UI, not page content.
    private static readonly HashSet<string> SkipAncestorTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "footer", "header", "nav", "aside", "form",
    };

    /// <summary>
    /// Returns true if the element is nested inside any element whose tag is in
    /// <see cref="SkipAncestorTags"/>. Used to exclude containers inside structural
    /// page chrome (footer nav columns, header dropdowns) and single-composition articles.
    /// </summary>
    private static bool HasSkipAncestor(IElement element)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (SkipAncestorTags.Contains(current.TagName))
                return true;
            current = current.ParentElement;
        }
        return false;
    }

    /// <summary>
    /// Returns true if the element contains SVG descendants whose combined text content
    /// exceeds <see cref="SvgTextLengthThreshold"/>. Such elements are diagram or
    /// visualisation containers (railroad syntax charts, data-vis SVGs) whose TextContent
    /// is dominated by keyword labels rather than prose, making them unsuitable candidates
    /// for repeated-item detection.
    /// </summary>
    private static bool HasSubstantialSvgContent(IElement element)
    {
        var svgs = element.QuerySelectorAll("svg");
        if (!svgs.Any()) return false;
        var svgTextLength = svgs.Sum(svg => svg.TextContent.Length);
        return svgTextLength > SvgTextLengthThreshold;
    }

    private static double ComputeLinkDensity(IElement element)
    {
        var totalText = element.TextContent.Trim().Length;
        if (totalText == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / totalText;
    }

    // Semantically self-typed tags: when items share one of these tags AND have no class
    // signal, the tag itself indicates a typed group (forum posts often use bare <article>
    // or <li> without classes). Generic containers (div/span) require a class signal.
    private static readonly HashSet<string> SelfTypedTags = new(StringComparer.OrdinalIgnoreCase)
    {
        "article", "section", "li",
    };

    private static bool HaveSimilarClassSignatures(IReadOnlyList<IElement> items)
    {
        // All items already share the same tag name (group key).
        // Require that at least MinClassOverlap fraction of class tokens are shared
        // across all items. Items with no class tokens pass only when the shared tag is
        // semantically self-typed (article/section/li). Generic containers (div/span)
        // with no class signal are most often style-only wrappers, not typed items.
        var classSets = items
            .Select(i => (i.ClassName ?? string.Empty)
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .ToHashSet(StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (classSets.All(s => s.Count == 0))
        {
            return SelfTypedTags.Contains(items[0].TagName);
        }

        // Intersection of all class sets.
        var intersect = new HashSet<string>(classSets[0], StringComparer.OrdinalIgnoreCase);
        for (int i = 1; i < classSets.Count; i++)
            intersect.IntersectWith(classSets[i]);

        var avgSize = classSets.Average(s => s.Count);
        if (avgSize == 0)
        {
            return SelfTypedTags.Contains(items[0].TagName);
        }

        return (double)intersect.Count / avgSize >= MinClassOverlap;
    }

    private static bool IsConsumed(IElement element, HashSet<IElement> consumed)
    {
        if (consumed.Contains(element)) return true;
        // Check if any ancestor is consumed (meaning element is inside an accepted group).
        var current = element.ParentElement;
        while (current is not null)
        {
            if (consumed.Contains(current)) return true;
            current = current.ParentElement;
        }
        return false;
    }

    private static void MarkConsumed(IElement element, HashSet<IElement> consumed)
    {
        consumed.Add(element);
        var stack = new Stack<IElement>();
        foreach (var child in element.Children) stack.Push(child);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            consumed.Add(current);
            foreach (var child in current.Children) stack.Push(child);
        }
    }

    private static IEnumerable<IElement> DescendantsIncludingSelf(IElement root)
    {
        yield return root;
        var stack = new Stack<IElement>();
        foreach (var child in root.Children) stack.Push(child);
        while (stack.Count > 0)
        {
            var current = stack.Pop();
            yield return current;
            foreach (var child in current.Children) stack.Push(child);
        }
    }
}
