using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class HeuristicBlockClassifier : IBlockClassifier
{
    // Roles for which at most one instance is meaningful per page.
    // Tables, CodeBlocks, and Forms are NOT singleton: a docs page commonly has many
    // code samples and a sidebar may have multiple forms. All others represent structural
    // sections that appear exactly once in a well-formed document.
    private static readonly HashSet<BlockRole> SingletonRoles = new()
    {
        BlockRole.MainContent,
        BlockRole.Article,
        BlockRole.Heading,
        BlockRole.Summary,
        BlockRole.Breadcrumb,
        BlockRole.PrimaryNavigation,
        BlockRole.Header,
        BlockRole.Footer,
    };

    private readonly string[] _footerPhrases;
    private readonly Regex[] _copyrightPatterns;
    private readonly string[] _cookiePhrases;
    private readonly HashSet<string> _navHints;
    private readonly HashSet<string> _adHints;
    private readonly HashSet<string> _frameworkContentHints;

    private HeuristicBlockClassifier(
        string[] footerPhrases,
        Regex[] copyrightPatterns,
        string[] cookiePhrases,
        HashSet<string> navHints,
        HashSet<string> adHints,
        HashSet<string> frameworkContentHints)
    {
        _footerPhrases = footerPhrases;
        _copyrightPatterns = copyrightPatterns;
        _cookiePhrases = cookiePhrases;
        _navHints = navHints;
        _adHints = adHints;
        _frameworkContentHints = frameworkContentHints;
    }

    public static HeuristicBlockClassifier LoadFromEmbeddedResources()
    {
        var assembly = typeof(HeuristicBlockClassifier).Assembly;

        PhraseList LoadPhraseList(string name)
        {
            var resName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var s = assembly.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize(s, HeuristicsJsonContext.Default.PhraseList)!;
        }

        PatternList LoadPatternList(string name)
        {
            var resName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var s = assembly.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize(s, HeuristicsJsonContext.Default.PatternList)!;
        }

        HintList LoadHintList(string name)
        {
            var resName = assembly.GetManifestResourceNames().Single(n => n.EndsWith(name, StringComparison.Ordinal));
            using var s = assembly.GetManifestResourceStream(resName)!;
            return JsonSerializer.Deserialize(s, HeuristicsJsonContext.Default.HintList)!;
        }

        var footer = LoadPhraseList("footer-phrases.json");
        var copyright = LoadPatternList("copyright-patterns.json");
        var cookie = LoadPhraseList("cookie-banner-phrases.json");
        var nav = LoadHintList("nav-class-hints.json");
        var ad = LoadHintList("ad-class-hints.json");
        var frameworkContent = LoadHintList("framework-content-class-hints.json");

        return new HeuristicBlockClassifier(
            footer.Phrases.ToArray(),
            copyright.Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray(),
            cookie.Phrases.ToArray(),
            new HashSet<string>(nav.Hints, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ad.Hints, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(frameworkContent.Hints, StringComparer.OrdinalIgnoreCase));
    }

    public IReadOnlyList<ExtractedBlock> Classify(IReadOnlyList<IElement> elements)
    {
        if (elements.Count == 0) return Array.Empty<ExtractedBlock>();

        // Step 1: Classify each candidate and compute a score.
        // The score drives non-overlapping subtree selection so that when
        // BlockSegmenter feeds us <main>, <article>, and their wrapper <div>s,
        // only the highest-scoring non-overlapping set is emitted.
        // (Fix 3 is subsumed by this selection: each emitted block's text is
        // disjoint from every other, so no separate text-dedup step is needed.)

        var candidates = new List<(IElement Element, BlockRole Role, double Confidence, double Score)>(elements.Count);
        foreach (var element in elements)
        {
            var (role, confidence) = ClassifyOne(element);
            var score = ComputeScore(element, role);
            candidates.Add((element, role, confidence, score));
        }

        // Step 1a: Suppress wrapper <div>/<section> candidates that ANCESTOR a semantic
        // <main>/<article> present in the candidate set. The wrapper's textLength includes
        // the chrome around the semantic element (top nav, footer, mega-menu), so its
        // fall-through score (textLength * linkPenalty^2) would otherwise win the greedy
        // non-overlapping selection over the actual <main>/<article>, which would then be
        // rejected as descendant. IntraBlockCleaner would later strip the nav-classed
        // descendants from the wrapper, leaving only residue.
        //
        // Crush the wrapper's score so the inner semantic tag wins greedy. The wrapper
        // remains in the candidate list (it might still be selected elsewhere as Boilerplate
        // context), but cannot beat its semantic descendant.
        //
        // Empirical: WCXB consumerreports.org page had <main> at 23523 chars; an outer
        // <div class="crux-container"> wrapper at 47k chars dominated selection, and
        // post-cleanup the emitted MainContent was 38 chars of residue.
        // Only suppress the wrapper if the semantic descendant has substantial NON-LINK
        // text. Two thresholds combine:
        //
        // 1. SubstantialSemanticTextThreshold (500 chars): collection/product pages commonly
        //    have an empty <main> containing only a breadcrumb and the real content lives in
        //    a sibling div. Suppressing the wrapper there would crush the only content
        //    candidate.
        //
        // 2. LinkDensityCeiling (0.5): REI / Etsy / e-commerce category pages have a <main>
        //    that contains 500-2000 chars of breadcrumb + intro + a 80%-link product grid.
        //    By textLength alone the <main> qualifies as substantial, but its actual prose
        //    content is tiny - the bulk is link text in product cards. Treating that <main>
        //    as "the article" forces IntraBlockCleaner to strip the link-heavy descendants,
        //    leaving 1 char of residue. WCXB v1.5.3 collection F1 -0.053 traces to 18 REI
        //    category pages each going F1 0.66-0.75 -> 0.00 with pred_chars=1.
        const int SubstantialSemanticTextThreshold = 500;
        const double SemanticElementMaxLinkDensity = 0.5;
        var semanticElements = new List<IElement>();
        for (int i = 0; i < candidates.Count; i++)
        {
            var t = candidates[i].Element.LocalName;
            if (t != "main" && t != "article") continue;
            var el = candidates[i].Element;
            if (el.TextContent.Trim().Length < SubstantialSemanticTextThreshold) continue;
            if (ComputeLinkDensity(el) >= SemanticElementMaxLinkDensity) continue;
            semanticElements.Add(el);
        }
        if (semanticElements.Count > 0)
        {
            for (int i = 0; i < candidates.Count; i++)
            {
                var c = candidates[i];
                var ctag = c.Element.LocalName;
                if (ctag != "div" && ctag != "section") continue;
                foreach (var sem in semanticElements)
                {
                    if (ReferenceEquals(c.Element, sem)) continue;
                    if (IsAncestor(c.Element, sem))
                    {
                        candidates[i] = (c.Element, BlockRole.Boilerplate, 0.3, -8000.0);
                        break;
                    }
                }
            }
        }

        // Step 1b: Repeated-item detection.
        // Find containers whose direct children form a homogeneous repeated-block
        // pattern (forum posts, listing cards, collection entries). When found:
        // - Each child item is injected as a RepeatedItem candidate with high confidence.
        // - The container element and other candidates that would overlap with these
        //   items are not added again (existing candidates remain; the greedy selector
        //   will prefer the RepeatedItem children over the container because the container
        //   will overlap with them and the children were injected with boosted scores).
        //
        // Step 1b entry: detection is not gated by main-element presence. The false-positive
        // guards inside RepeatedItemDetector (SkipAncestorTags, SkipContainerTags,
        // SkipChildTags, link-density filter, wrapping-ratio check) are sufficient to prevent
        // misfires on documentation and article pages without needing a global gate.
        if (elements.Count > 0)
        {
            var documentRoot = elements[0].Owner?.Body ?? elements[0].ParentElement;
            if (documentRoot is not null)
            {
                var allGroups = RepeatedItemDetector.Detect(documentRoot);

                var repeatedGroups = allGroups;

                if (repeatedGroups.Count > 0)
                {
                    // Demote the containers: mark as Boilerplate so the non-overlapping
                    // step deprioritises them in favour of the RepeatedItem children.
                    // EXCEPTION 1: if the container has substantial extra text beyond the
                    // items' combined text (ratio < 0.85), the container holds "wrapper"
                    // content (intro paragraphs, summary sections) that we must not lose.
                    // EXCEPTION 2: if the container is nested inside a MainContent / Article
                    // candidate, the group is a sub-widget (related-posts grid, recommended
                    // articles, "people also read") embedded INSIDE the main article. The
                    // article is the page's actual content; the widget is not. Skipping
                    // injection lets the main article win selection. WordPress / Squarespace /
                    // Ghost templates routinely embed 3-6 related-post cards inside the post's
                    // own <article> wrapper, which previously suppressed the main content
                    // entirely via overlap-rejection (the cards win at score 50000 each, then
                    // the main article is rejected as their ancestor).
                    const double ContainerWrappingRatioThreshold = 0.85;
                    // Exception 2 threshold: a group's items must occupy at least this fraction
                    // of their MainContent/Article ancestor's text to count as "the real content".
                    // Below this they are a sub-widget (related-posts grid, recommended articles)
                    // embedded inside the actual main content, and injecting them would suppress
                    // the main content via overlap-rejection. WordPress / Squarespace templates
                    // routinely show 3-6 related-post cards (each ~150 chars: title + excerpt)
                    // inside a 15k-char article wrapper — that's 4%, well below this threshold.
                    // A real forum thread's posts span essentially the full main element (>90%).
                    const double ItemsFractionOfMainContentThreshold = 0.60;

                    var mainContentCandidates = candidates
                        .Where(c => c.Role is BlockRole.MainContent or BlockRole.Article)
                        .Select(c => c.Element)
                        .ToList();

                    var groupsToInject = new List<RepeatedItemGroup>(repeatedGroups.Count);
                    foreach (var g in repeatedGroups)
                    {
                        // Exception 2: skip if container is nested inside a MainContent/Article
                        // candidate AND the group's items occupy a small fraction of that
                        // candidate's text (the group is a sub-widget, not the page's content).
                        var mainAncestor = FindAncestorIn(g.Container, mainContentCandidates);
                        if (mainAncestor is not null)
                        {
                            var ancestorTextLen = mainAncestor.TextContent.Trim().Length;
                            var itemsTotalText = g.Items.Sum(item => item.TextContent.Trim().Length);
                            var itemsFractionOfAncestor = ancestorTextLen > 0
                                ? (double)itemsTotalText / ancestorTextLen
                                : 0;
                            if (itemsFractionOfAncestor < ItemsFractionOfMainContentThreshold)
                            {
                                continue;
                            }
                        }

                        var containerTextLen = g.Container.TextContent.Trim().Length;
                        var itemsTextLen = g.Items.Sum(item => item.TextContent.Trim().Length);
                        var ratio = containerTextLen > 0 ? (double)itemsTextLen / containerTextLen : 0;

                        if (ratio >= ContainerWrappingRatioThreshold)
                        {
                            // Container is mostly just a wrapper for the items. Safe to replace.
                            groupsToInject.Add(g);
                        }
                        // else: container has significant extra content; leave it as-is.
                    }

                    // Demote containers only for groups that passed the ratio check.
                    var containersToSuppress = new HashSet<IElement>(
                        groupsToInject.Select(g => g.Container),
                        ReferenceEqualityComparer.Instance);
                    for (int i = 0; i < candidates.Count; i++)
                    {
                        if (containersToSuppress.Contains(candidates[i].Element))
                        {
                            candidates[i] = (candidates[i].Element, BlockRole.Boilerplate, 0.1, -9999.0);
                        }
                    }
                    // Rebuild the groups list to only include groups we're actually injecting.
                    repeatedGroups = groupsToInject;

                    // Inject RepeatedItem candidates for each item in each group.
                    // Give them a very high score so they win the greedy selection.
                    foreach (var group in repeatedGroups)
                    {
                        foreach (var item in group.Items)
                        {
                            // Only inject if not already present in the candidate list.
                            bool alreadyPresent = candidates.Any(c => ReferenceEquals(c.Element, item));
                            if (!alreadyPresent)
                            {
                                candidates.Add((item, BlockRole.RepeatedItem, 0.9, 50000.0));
                            }
                            else
                            {
                                // Override the existing candidate entry to RepeatedItem.
                                for (int i = 0; i < candidates.Count; i++)
                                {
                                    if (ReferenceEquals(candidates[i].Element, item))
                                    {
                                        candidates[i] = (item, BlockRole.RepeatedItem, 0.9, 50000.0);
                                        break;
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }

        // Step 2: Sort by score descending for greedy selection.
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Step 3: Greedy non-overlapping selection.
        // Accept a candidate only if it is not an ancestor or descendant of any
        // already-accepted element. This ensures disjoint text coverage.
        //
        // Two hash sets make both directions of the overlap check linear in DOM
        // depth instead of O(N_accepted * depth) per candidate. On the WCXB Large
        // input (~1500-node DOM, ~300 candidates) the original `accepted.Any(IsAncestor)`
        // loop accounted for ~25.9 MB of per-call allocation and a substantial
        // share of Classify time (bench 0496369). This rewrite keeps behavior
        // identical: a candidate is rejected iff it shares any element along its
        // ancestor path with an accepted element, in either direction.
        var accepted = new List<(IElement Element, BlockRole Role, double Confidence)>(candidates.Count);
        var acceptedElements = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
        var ancestorsOfAccepted = new HashSet<IElement>(ReferenceEqualityComparer.Instance);
        foreach (var (element, role, confidence, _) in candidates)
        {
            // Overlap-as-descendant: walk up from candidate looking for any accepted ancestor.
            bool overlaps = false;
            var cur = element.ParentElement;
            while (cur is not null)
            {
                if (acceptedElements.Contains(cur)) { overlaps = true; break; }
                cur = cur.ParentElement;
            }
            // Overlap-as-ancestor: O(1) lookup against precomputed ancestor set.
            if (!overlaps && ancestorsOfAccepted.Contains(element)) overlaps = true;

            if (!overlaps)
            {
                accepted.Add((element, role, confidence));
                acceptedElements.Add(element);
                // Record candidate's ancestor path so future "is candidate an ancestor of
                // an accepted?" checks are O(1). The early break exploits the fact that if
                // a parent is already in the set, all its ancestors are too.
                var anc = element.ParentElement;
                while (anc is not null && ancestorsOfAccepted.Add(anc))
                {
                    anc = anc.ParentElement;
                }
            }
        }

        // Step 3b: Relative quality gate for MainContent blocks.
        // When a high-scoring MainContent/Article block is present, suppress
        // low-scoring MainContent blocks that are likely navigation wrappers or
        // boilerplate mistakenly classified as content (e.g. Wikipedia's sidebar
        // menus, which have ~450 chars at ~0.45 link density and score ~86, while
        // the actual article wrapper scores ~18000+).
        // Only applies when there is at least one accepted content block scoring
        // above the "high-content" threshold. Content-role blocks scoring below 25%
        // of the best content score are demoted to Boilerplate so the renderer drops them.
        // v1.2.2 used 5%; tightened to 25% in v1.2.3 because 5% still let large-sidebar
        // wrappers through on long articles where their absolute text length kept them
        // above the floor. Wikipedia's "Main menu" wrapper at 8% of the article body's
        // score is exactly this case.
        var bestContentScore = candidates
            .Where(c => c.Role is BlockRole.MainContent or BlockRole.Article)
            .Select(c => c.Score)
            .DefaultIfEmpty(0)
            .Max();
        const double ContentQualityRatio = 0.25; // 25% of the best content score
        const double HighContentThreshold = 1000.0; // only activate gate when real content exists
        if (bestContentScore >= HighContentThreshold)
        {
            var scoreMap = new Dictionary<IElement, double>(ReferenceEqualityComparer.Instance);
            foreach (var (el, _, _, sc) in candidates) scoreMap[el] = sc;
            for (int i = 0; i < accepted.Count; i++)
            {
                var (el, role, conf) = accepted[i];
                if (role is BlockRole.MainContent or BlockRole.Article)
                {
                    var sc = scoreMap.TryGetValue(el, out var s) ? s : 0;
                    if (sc < bestContentScore * ContentQualityRatio)
                    {
                        accepted[i] = (el, BlockRole.Boilerplate, conf);
                    }
                }
            }
        }

        // Step 3c: Top-K per role cap.
        // A single page has at most ONE main content block, ONE primary nav, ONE breadcrumb,
        // ONE summary, ONE page heading. Tables, code blocks, and forms can repeat (a docs
        // page often has many code samples). For singleton roles, keep only the highest-
        // scoring instance and drop the rest. This prevents multiple disjoint secondary
        // blocks (nav wrappers, TOC, toolbar) from all passing the previous filters and
        // inflating output on long pages (Wikipedia ASP.NET, MS Docs AOT).
        var scoreMapForRoleCap = new Dictionary<IElement, double>(ReferenceEqualityComparer.Instance);
        foreach (var (el, _, _, sc) in candidates) scoreMapForRoleCap[el] = sc;

        // Within a singleton role, a candidate from the semantic-tag classification path
        // (conf >= 0.70, returned for <main>/<article>/<header>/<nav>/<aside>) ALWAYS
        // beats a fallthrough candidate (conf 0.50, returned for div/section with text > 200),
        // regardless of raw score. This fixes the WCXB consumerreports / Shopify pattern
        // where a 47k-char wrapper div (mega-menu + chrome) outscored a 14k-char <main> on
        // textLength * linkPenalty^2 and won MainContent, leaving IntraBlockCleaner to strip
        // the mega-menu and emit ~38 chars of residue. Score-only comparison ignored the
        // explicit semantic intent encoded by the page author's <main>/<article> tag.
        //
        // The rule has one exception: an "empty" semantic wrapper does NOT win automatically.
        // WordPress + SNOFlex / similar themes routinely render <main> as a near-empty CSS
        // scaffold (just inline <style> tags consumed by DomCleaner) while the real article
        // body lives in a deeper <div> matching no framework-content hint. Without an
        // emptiness check, the semantic-tag-wins rule keeps the empty <main> and rejects
        // the rich div — yielding ~1 char of MainContent output. WCXB diagnostic
        // (2026-06-24): 10+ pages emit pred_chars=1, all matching this shape. Threshold
        // shared with SubstantialSemanticTextThreshold above.
        const double SemanticTagConfidenceFloor = 0.70;
        const int EmptySemanticWrapperTextCeiling = SubstantialSemanticTextThreshold;
        bool IsSemanticTagCandidate(IElement el, double conf)
        {
            if (conf < SemanticTagConfidenceFloor) return false;
            var tag = el.LocalName;
            if (tag is not ("main" or "article" or "header" or "nav" or "aside" or "footer"))
                return false;
            // Empty wrapper: WP/SNOFlex/etc <main> with only <style>/whitespace.
            // Refuse semantic-priority so a richer content descendant can win on score.
            return el.TextContent.Trim().Length >= EmptySemanticWrapperTextCeiling;
        }

        var bestPerSingletonRole = new Dictionary<BlockRole, (IElement Element, double Score, bool IsSemantic)>();
        foreach (var (el, role, conf) in accepted)
        {
            if (!SingletonRoles.Contains(role)) continue;
            var sc = scoreMapForRoleCap.TryGetValue(el, out var s) ? s : 0;
            var isSemantic = IsSemanticTagCandidate(el, conf);
            if (!bestPerSingletonRole.TryGetValue(role, out var current))
            {
                bestPerSingletonRole[role] = (el, sc, isSemantic);
                continue;
            }
            // Semantic-tag candidate beats fallthrough regardless of score.
            // Within the same tier (both semantic or both fallthrough), highest score wins.
            if (isSemantic && !current.IsSemantic)
            {
                bestPerSingletonRole[role] = (el, sc, true);
            }
            else if (isSemantic == current.IsSemantic && sc > current.Score)
            {
                bestPerSingletonRole[role] = (el, sc, isSemantic);
            }
        }

        // Keep accepted entries: non-singleton roles pass through; singleton roles keep only the winner.
        var acceptedAfterRoleCap = new List<(IElement Element, BlockRole Role, double Confidence)>(accepted.Count);
        foreach (var (el, role, conf) in accepted)
        {
            if (!SingletonRoles.Contains(role))
            {
                acceptedAfterRoleCap.Add((el, role, conf));
            }
            else if (bestPerSingletonRole.TryGetValue(role, out var winner) && ReferenceEquals(winner.Element, el))
            {
                acceptedAfterRoleCap.Add((el, role, conf));
            }
            // else: a lower-scoring (or non-semantic) duplicate of a singleton role — drop it
        }
        accepted = acceptedAfterRoleCap;

        // Step 3d: Intra-block cleaning pass (v1.3).
        // For each accepted block in a content-bearing role, walk descendants and remove
        // nav/toc/toolbar/breadcrumb elements that are nested inside the selected subtree.
        // This handles the case where <main> (or <article>) is the highest-quality block
        // but still contains an internal TOC, action bar, or breadcrumb as a descendant.
        // The IDocument is mutated in place; the pipeline owns the document for its lifetime.
        // After removal, step 5 reads element.TextContent directly so the cleaned values
        // are picked up automatically without a separate re-derive step.
        foreach (var (el, role, _) in accepted)
        {
            if (role is BlockRole.MainContent or BlockRole.Article
                or BlockRole.Heading or BlockRole.Summary or BlockRole.Breadcrumb
                or BlockRole.RepeatedItem)
            {
                IntraBlockCleaner.Clean(el);
            }
        }

        // Step 4: Re-sort accepted set by DOM order so the renderer emits in reading order.
        // Use the original element list index as a stable DOM-order proxy for segmented elements.
        // For injected RepeatedItem elements (not in the original list), compute a positional
        // key by walking from the document root to assign a pre-order traversal index. This
        // ensures post-injection items appear in their natural reading position.
        var indexMap = new Dictionary<IElement, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < elements.Count; i++)
        {
            indexMap[elements[i]] = i;
        }

        // Pre-order traversal to assign document order for elements not in the original list.
        // Scale by a large offset so these indices interleave correctly with original ones.
        if (accepted.Any(a => !indexMap.ContainsKey(a.Element)))
        {
            var bodyRoot = elements.Count > 0 ? elements[0].Owner?.Body : null;
            if (bodyRoot is not null)
            {
                int preOrderIndex = 0;
                var preOrderMap = new Dictionary<IElement, int>(ReferenceEqualityComparer.Instance);
                var stack = new Stack<IElement>();
                stack.Push(bodyRoot);
                while (stack.Count > 0)
                {
                    var cur = stack.Pop();
                    preOrderMap[cur] = preOrderIndex++;
                    foreach (var child in cur.Children.Reverse())
                        stack.Push(child);
                }

                // Remap: find position within pre-order space and map to a fractional index
                // relative to the known segments. We scale each pre-order position to sit
                // within the [0, elements.Count * 1000] range using the document size.
                int docSize = Math.Max(preOrderIndex, 1);
                int scale = Math.Max(elements.Count, 1) * 1000;
                foreach (var (el, _, _) in accepted)
                {
                    if (!indexMap.ContainsKey(el) && preOrderMap.TryGetValue(el, out var po))
                    {
                        indexMap[el] = (int)((long)po * scale / docSize);
                    }
                }
            }
        }

        accepted.Sort((a, b) =>
        {
            var ai = indexMap.TryGetValue(a.Element, out var ia) ? ia : int.MaxValue;
            var bi = indexMap.TryGetValue(b.Element, out var ib) ? ib : int.MaxValue;
            return ai.CompareTo(bi);
        });

        // Step 5: Construct ExtractedBlock records from the accepted, ordered set.
        var result = new List<ExtractedBlock>(accepted.Count);
        int blockIndex = 0;
        foreach (var (element, role, confidence) in accepted)
        {
            result.Add(new ExtractedBlock
            {
                Id = $"b{blockIndex:D4}",
                Role = role,
                Confidence = confidence,
                Text = element.TextContent.Trim(),
                Markdown = ShouldRenderMarkdown(role) ? DomMarkdownWalker.Render(element) : "",
                XPath = XPathBuilder.ComputeXPath(element),
                CssSelector = null,
                TextLength = element.TextContent.Length,
                LinkDensity = ComputeLinkDensity(element),
                Links = ExtractLinks(element)
            });
            blockIndex++;
        }
        return result;
    }

    // Markdown is walked for any role whose content has structure worth preserving:
    // body content (MainContent / Article / RepeatedItem / Summary / Heading), tables
    // and code, and the auxiliary content surfaces (Sidebar, RelatedLinks). Sidebars
    // and related-links blocks routinely host TOCs and on-this-page lists that read
    // as plain-text noise without the DOM walker. Navigation, breadcrumb, form, and
    // footer keep their role-specific projections in BlockRoleRenderers which beat a
    // generic walk on link-list shapes.
    private static bool ShouldRenderMarkdown(BlockRole role) => role
        is BlockRole.MainContent
        or BlockRole.Article
        or BlockRole.RepeatedItem
        or BlockRole.Summary
        or BlockRole.Heading
        or BlockRole.Table
        or BlockRole.CodeBlock
        or BlockRole.Sidebar
        or BlockRole.RelatedLinks;

    private double ComputeScore(IElement element, BlockRole role)
    {
        var textLength = element.TextContent.Trim().Length;
        var linkDensity = ComputeLinkDensity(element);

        // Squared link-density penalty: high-link-density blocks (navigation, listings, related
        // links) should not outscore content blocks just because their wrapper has more total
        // text. Sidebar 10000 chars at 0.75 link density gives 625; article body 2500 chars at
        // 0.05 link density gives 2255. Without the square, sidebar wins (2500 vs 2375).
        var linkPenalty = (1.0 - linkDensity);
        var baseScore = textLength * linkPenalty * linkPenalty;

        // Role-based bonus: strongly prefer content-bearing roles so that when
        // overlapping elements compete (e.g. <main> vs its wrapper <div>),
        // the semantically richest element wins.
        // Navigation, footer, header etc. get a small positive bonus so they
        // still beat their generic wrapper ancestors.
        var roleBonus = role switch
        {
            BlockRole.MainContent or BlockRole.Article => 500.0,
            BlockRole.Table or BlockRole.CodeBlock => 200.0,
            BlockRole.Form or BlockRole.Summary => 100.0,
            BlockRole.Heading => 50.0,
            BlockRole.PrimaryNavigation or BlockRole.SecondaryNavigation
                or BlockRole.Breadcrumb => 50.0,
            BlockRole.Sidebar or BlockRole.RelatedLinks => 20.0,
            BlockRole.Footer or BlockRole.Header
                or BlockRole.CookieBanner or BlockRole.Advertisement => 10.0,
            BlockRole.Boilerplate or BlockRole.Unknown => -200.0,
            _ => 0.0
        };

        // Tag-based nudge: prefer semantically specific tags over generic wrappers
        // when both are classified as the same role. Only <div> and <section> get a
        // mild penalty; named semantic tags are already captured in roleBonus.
        var tag = element.LocalName;
        var tagNudge = tag is "div" ? -50.0 : 0.0;

        // Class/id hint bonus: only applies when role is MainContent (wrapper divs with
        // content-class/id names should yield to <article>/<main> siblings).
        // Both class tokens and the id attribute are checked so that CMS patterns using
        // id-only identifiers (e.g. id="mw-panel", id="footer", id="mw-navigation")
        // are treated identically to class-based hints.
        double classHintBonus = 0.0;
        if (role is BlockRole.MainContent or BlockRole.Boilerplate)
        {
            // ContentTokenAny / ChromeTokenAny avoid the prior path's three allocations
            // per token: a Split() string[] for the class attribute, a ToLowerInvariant
            // string per token, and a LINQ Append wrapper for the id pool. Both checks
            // now walk the class span without copying, then check id once.
            if (HasAnyHintToken(element, ContentHints)) classHintBonus += 300.0;
            if (HasAnyHintToken(element, ChromeHints)) classHintBonus -= 300.0;
        }

        return baseScore + roleBonus + tagNudge + classHintBonus;
    }

    private static readonly string[] ContentHints =
        { "content", "article", "post", "main", "entry" };

    private static readonly string[] ChromeHints =
        { "ad", "advertisement", "sidebar", "nav", "menu", "footer",
          "header", "widget", "comments" };

    // Returns true if any class token OR the id contains any hint as a substring
    // (case-insensitive). Same semantics as the prior Split() + ToLowerInvariant()
    // + Contains() chain, with zero allocation on the typical path.
    private static bool HasAnyHintToken(IElement element, string[] hints)
    {
        var classAttr = element.GetAttribute("class");
        if (!string.IsNullOrEmpty(classAttr))
        {
            ReadOnlySpan<char> remaining = classAttr.AsSpan();
            while (remaining.Length > 0)
            {
                int spaceIdx = remaining.IndexOf(' ');
                ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];
                if (token.Length > 0 && AnyHintMatchesSpan(token, hints)) return true;
                if (spaceIdx < 0) break;
                remaining = remaining[(spaceIdx + 1)..];
            }
        }
        var id = element.GetAttribute("id");
        if (!string.IsNullOrEmpty(id) && AnyHintMatchesSpan(id.AsSpan(), hints))
            return true;
        return false;
    }

    private static bool AnyHintMatchesSpan(ReadOnlySpan<char> token, string[] hints)
    {
        foreach (var hint in hints)
        {
            if (token.Contains(hint.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private (BlockRole Role, double Confidence) ClassifyOne(IElement element)
    {
        var tag = element.LocalName;
        var text = element.TextContent;
        var linkDensity = ComputeLinkDensity(element);

        bool TextContainsAny(IEnumerable<string> phrases) =>
            phrases.Any(p => text.Contains(p, StringComparison.OrdinalIgnoreCase));

        bool TextMatchesAny(Regex[] patterns) => patterns.Any(r => r.IsMatch(text));

        // Semantic <nav> tags are always navigation regardless of link density.
        // A TOC or sidebar <nav> with LD=0.59 is still navigation, not content.
        // For class/id hint matches on non-nav tags, keep the LD > 0.7 guard to
        // avoid false positives on content divs with nav-sounding class names.
        if (tag is "nav")
        {
            var isPrimaryNav = HasAncestorTag(element, "header") || GetDepth(element) <= 3;
            return (isPrimaryNav ? BlockRole.PrimaryNavigation : BlockRole.SecondaryNavigation, 0.85);
        }
        if (IdOrClassMatches(element, _navHints))
        {
            if (linkDensity > 0.7)
            {
                var isPrimary = HasAncestorTag(element, "header") || GetDepth(element) <= 3;
                return (isPrimary ? BlockRole.PrimaryNavigation : BlockRole.SecondaryNavigation, 0.85);
            }
        }

        if (tag == "footer" || ClassAttrContains(element, "footer"))
        {
            if (TextContainsAny(_footerPhrases) || TextMatchesAny(_copyrightPatterns))
            {
                return (BlockRole.Footer, 0.9);
            }
            return (BlockRole.Footer, 0.6);
        }

        if (tag == "header") return (BlockRole.Header, 0.7);

        if (tag is "main" or "article")
        {
            // Always treat <main> and <article> as MainContent regardless of text length.
            // Short <main>/<article> elements still win over generic div wrappers via scoring.
            return (BlockRole.MainContent, text.Length > 200 ? 0.92 : 0.70);
        }

        // Framework-emitted content wrappers: CMS templates commonly wrap the article body
        // in a recognised class like `entry-content` (WordPress), `post-content` (WordPress /
        // Ghost), `wp-block-post-content` (Gutenberg), `gh-content` / `kg-content` (Ghost
        // Casper), `field--name-body` (Drupal), `magento-content-area` (Magento), etc.
        // These hints live in Definitions/framework-content-class-hints.json so adding
        // patterns for new frameworks is data, not code.
        //
        // Linkbase guard: if the class-matched element is overwhelmingly links (>= 0.6),
        // it is more likely a related-posts/category-nav widget that happens to use a
        // content-sounding class, so fall through to other classification.
        if ((tag == "div" || tag == "section") && IdOrClassMatches(element, _frameworkContentHints))
        {
            if (linkDensity < 0.6)
            {
                return (BlockRole.MainContent, text.Length > 200 ? 0.92 : 0.70);
            }
        }

        if (tag == "aside")
        {
            return (linkDensity > 0.5 ? BlockRole.RelatedLinks : BlockRole.Sidebar, 0.75);
        }

        // Fix 4: tighten Form classification so mobile-nav toggles and pure-button
        // forms don't get classified as Form. Only classify as Form when there is
        // at least one meaningful text-entry input or textarea.
        //
        // Body-spanning form exception: ASP.NET WebForms and some legacy CMSs wrap
        // the ENTIRE page in a single <form id="aspnetForm"> with one hidden
        // ViewState input plus a search box somewhere. That trips the meaningful-
        // input check and classifies the whole page body as Form, suppressing all
        // its descendant content candidates via overlap rejection. WCXB diagnostic:
        // drainblasterbill, Google Sites pages, several .aspx hosts produced
        // pred_chars=1 from this pattern. Heuristic: if the form's text is more
        // than 70% of the body's text, treat it as a content wrapper, not a form.
        if (tag == "form")
        {
            var doc = element.Owner;
            var bodyText = doc?.Body?.TextContent.Length ?? 0;
            var formText = element.TextContent.Length;
            // Substantial-size guard: only ASP.NET-style wrappers (kilobytes of
            // text) get this treatment, not tiny test/contact forms.
            const int BodySpanningMinFormText = 500;
            var isBodySpanning = bodyText > 0
                                 && formText >= BodySpanningMinFormText
                                 && (double)formText / bodyText >= 0.7;

            if (!isBodySpanning)
            {
                var meaningfulInputs = element.QuerySelectorAll("input, textarea")
                    .Count(input =>
                    {
                        if (input.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
                            return true;
                        var type = (input.GetAttribute("type") ?? "text").ToLowerInvariant();
                        return type is "text" or "email" or "search" or "tel" or "url"
                               or "number" or "password" or "date" or "datetime-local"
                               or "month" or "week" or "time";
                    });
                if (meaningfulInputs >= 1)
                {
                    return (BlockRole.Form, 0.85);
                }
            }
            // Body-spanning forms fall through to the default classification
            // path so their content descendants can win via overlap selection.
            // form with no meaningful inputs: fall through to default classification
        }
        // Note: We DO NOT classify a non-<form> element as Form just because it contains
        // inputs in its descendants. Almost every CMS page wrapper contains a search form
        // plus a comment/newsletter form (>= 2 inputs) reachable via QuerySelectorAll;
        // classifying the page wrapper as Form would suppress the actual main content via
        // overlap-rejection (the Form-classified wrapper outscores its descendants). Real
        // forms must use the <form> tag.

        if (tag == "table") return (BlockRole.Table, 0.95);

        if (TextContainsAny(_cookiePhrases) && element.QuerySelector("button") is not null)
        {
            return (BlockRole.CookieBanner, 0.9);
        }

        if (IdOrClassMatches(element, _adHints) && linkDensity > 0.5)
        {
            return (BlockRole.Advertisement, 0.8);
        }

        var finalRole = text.Length > 200 ? BlockRole.MainContent : BlockRole.Boilerplate;
        var finalConfidence = text.Length > 200 ? 0.5 : 0.3;

        // Hard cap: a block that is overwhelmingly links cannot be MainContent regardless of
        // how much total text it has. Wikipedia's left sidebar is the canonical example.
        // Demote MainContent/Article role to PrimaryNavigation if link density >= 0.6.
        if (linkDensity >= 0.6 && (finalRole == BlockRole.MainContent || finalRole == BlockRole.Article))
        {
            var depth = GetDepth(element);
            return (depth <= 3 ? BlockRole.PrimaryNavigation : BlockRole.SecondaryNavigation, 0.85);
        }

        return (finalRole, finalConfidence);
    }

    private static double ComputeLinkDensity(IElement element)
    {
        var totalText = element.TextContent.Length;
        if (totalText == 0) return 0;
        // Manual descendant walk instead of QuerySelectorAll("a").Sum(...) — that path
        // allocates an IElementCollection wrapper + a LINQ enumerator iterator on every
        // call. ComputeLinkDensity runs at least twice per candidate (once on the
        // semantic-element scan, once inside ComputeScore) so a 50-candidate page paid
        // ~100 allocations for what's a single tree walk. SumLinkText is iterative to
        // avoid recursion blow-up on Wikipedia-shaped trees.
        var linkText = SumLinkText(element);
        return (double)linkText / totalText;
    }

    // Case-insensitive contains check against any class token, no allocation.
    private static bool ClassAttrContains(IElement element, string needle)
    {
        var classAttr = element.GetAttribute("class");
        if (string.IsNullOrEmpty(classAttr)) return false;
        ReadOnlySpan<char> remaining = classAttr.AsSpan();
        while (remaining.Length > 0)
        {
            int spaceIdx = remaining.IndexOf(' ');
            ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];
            if (token.Length > 0 && token.Contains(needle.AsSpan(), StringComparison.OrdinalIgnoreCase))
                return true;
            if (spaceIdx < 0) break;
            remaining = remaining[(spaceIdx + 1)..];
        }
        return false;
    }

    private static int SumLinkText(IElement root)
    {
        int total = 0;
        var stack = new Stack<IElement>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var cur = stack.Pop();
            if (cur.LocalName == "a") total += cur.TextContent.Length;
            foreach (var child in cur.Children) stack.Push(child);
        }
        return total;
    }

    private static IReadOnlyList<ExtractedLink> ExtractLinks(IElement element)
    {
        return element.QuerySelectorAll("a")
            .Select(a => new ExtractedLink
            {
                Text = a.TextContent.Trim(),
                Href = a.GetAttribute("href") ?? "",
                IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
            })
            .ToList();
    }

    private static bool HasAncestorTag(IElement element, string tag)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (current.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase)) return true;
            current = current.ParentElement;
        }
        return false;
    }

    private static int GetDepth(IElement element)
    {
        int depth = 0;
        var current = element.ParentElement;
        while (current is not null) { depth++; current = current.ParentElement; }
        return depth;
    }

    /// <summary>
    /// Returns true when any hint substring matches a class token OR the element's id
    /// attribute. This handles CMS patterns (Drupal, older WordPress, Wikipedia) that use
    /// id-only identifiers like id="mw-panel" or id="footer" instead of class names.
    /// </summary>
    private static bool IdOrClassMatches(IElement element, HashSet<string> hints)
    {
        // Tokenise the class attribute without allocating a string[]. We walk the span
        // looking for whitespace separators, then check each token slice against every
        // hint via case-insensitive Contains over spans. Same semantics as the prior
        // Split + LINQ chain (any token contains any hint as a substring), with zero
        // allocation on the typical path.
        var classAttr = element.GetAttribute("class");
        if (!string.IsNullOrEmpty(classAttr))
        {
            ReadOnlySpan<char> remaining = classAttr.AsSpan();
            while (remaining.Length > 0)
            {
                int spaceIdx = remaining.IndexOf(' ');
                ReadOnlySpan<char> token = spaceIdx < 0 ? remaining : remaining[..spaceIdx];
                if (token.Length > 0)
                {
                    foreach (var hint in hints)
                    {
                        if (token.Contains(hint.AsSpan(), StringComparison.OrdinalIgnoreCase))
                            return true;
                    }
                }
                if (spaceIdx < 0) break;
                remaining = remaining[(spaceIdx + 1)..];
            }
        }

        var id = element.GetAttribute("id");
        if (!string.IsNullOrEmpty(id))
        {
            ReadOnlySpan<char> idSpan = id.AsSpan();
            foreach (var hint in hints)
            {
                if (idSpan.Contains(hint.AsSpan(), StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if <paramref name="potentialAncestor"/> is an ancestor of
    /// <paramref name="potentialDescendant"/> in the DOM tree.
    /// Used by the non-overlapping selection step to avoid emitting the same
    /// text content in multiple blocks.
    /// </summary>
    private static bool IsAncestor(IElement potentialAncestor, IElement potentialDescendant)
    {
        var current = potentialDescendant.ParentElement;
        while (current is not null)
        {
            if (ReferenceEquals(current, potentialAncestor)) return true;
            current = current.ParentElement;
        }
        return false;
    }

    // Walk up from element; return the first ancestor that is in the candidate list,
    // or null if no ancestor matches. O(depth × |candidates|).
    private static IElement? FindAncestorIn(IElement element, IReadOnlyList<IElement> candidates)
    {
        if (candidates.Count == 0) return null;
        var current = element.ParentElement;
        while (current is not null)
        {
            foreach (var c in candidates)
            {
                if (ReferenceEquals(c, current)) return current;
            }
            current = current.ParentElement;
        }
        return null;
    }
}
