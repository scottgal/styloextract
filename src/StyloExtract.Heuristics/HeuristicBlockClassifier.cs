using System.Text.Json;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class HeuristicBlockClassifier : IBlockClassifier
{
    private readonly string[] _footerPhrases;
    private readonly Regex[] _copyrightPatterns;
    private readonly string[] _cookiePhrases;
    private readonly HashSet<string> _navHints;
    private readonly HashSet<string> _adHints;

    private HeuristicBlockClassifier(
        string[] footerPhrases,
        Regex[] copyrightPatterns,
        string[] cookiePhrases,
        HashSet<string> navHints,
        HashSet<string> adHints)
    {
        _footerPhrases = footerPhrases;
        _copyrightPatterns = copyrightPatterns;
        _cookiePhrases = cookiePhrases;
        _navHints = navHints;
        _adHints = adHints;
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

        return new HeuristicBlockClassifier(
            footer.Phrases.ToArray(),
            copyright.Patterns.Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled)).ToArray(),
            cookie.Phrases.ToArray(),
            new HashSet<string>(nav.Hints, StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(ad.Hints, StringComparer.OrdinalIgnoreCase));
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

        // Step 2: Sort by score descending for greedy selection.
        candidates.Sort((a, b) => b.Score.CompareTo(a.Score));

        // Step 3: Greedy non-overlapping selection.
        // Accept a candidate only if it is not an ancestor or descendant of any
        // already-accepted element. This ensures disjoint text coverage.
        var accepted = new List<(IElement Element, BlockRole Role, double Confidence)>(candidates.Count);
        foreach (var (element, role, confidence, _) in candidates)
        {
            bool overlaps = accepted.Any(a =>
                IsAncestor(a.Element, element) || IsAncestor(element, a.Element));
            if (!overlaps)
            {
                accepted.Add((element, role, confidence));
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

        // Step 4: Re-sort accepted set by DOM order so the renderer emits in reading order.
        // Use the original element list index as a stable DOM-order proxy.
        var indexMap = new Dictionary<IElement, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < elements.Count; i++)
        {
            indexMap[elements[i]] = i;
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
                Markdown = "",
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
        var tag = element.TagName.ToLowerInvariant();
        var tagNudge = tag is "div" ? -50.0 : 0.0;

        // Class/id hint bonus: only applies when role is MainContent (wrapper divs with
        // content-class/id names should yield to <article>/<main> siblings).
        // Both class tokens and the id attribute are checked so that CMS patterns using
        // id-only identifiers (e.g. id="mw-panel", id="footer", id="mw-navigation")
        // are treated identically to class-based hints.
        double classHintBonus = 0.0;
        if (role is BlockRole.MainContent or BlockRole.Boilerplate)
        {
            var classTokens = (element.GetAttribute("class") ?? "")
                .Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var idAttr = element.GetAttribute("id") ?? "";
            // Combine class tokens and id as one pool of tokens to check.
            var allHintTokens = idAttr.Length > 0
                ? classTokens.Append(idAttr)
                : classTokens;
            foreach (var token in allHintTokens)
            {
                var t = token.ToLowerInvariant();
                if (t.Contains("content") || t.Contains("article") || t.Contains("post")
                    || t.Contains("main") || t.Contains("entry"))
                {
                    classHintBonus += 300.0;
                    break;
                }
            }
            foreach (var token in allHintTokens)
            {
                var t = token.ToLowerInvariant();
                if (t.Contains("ad") || t.Contains("advertisement") || t.Contains("sidebar")
                    || t.Contains("nav") || t.Contains("menu") || t.Contains("footer")
                    || t.Contains("header") || t.Contains("widget") || t.Contains("comments"))
                {
                    classHintBonus -= 300.0;
                    break;
                }
            }
        }

        return baseScore + roleBonus + tagNudge + classHintBonus;
    }

    private (BlockRole Role, double Confidence) ClassifyOne(IElement element)
    {
        var tag = element.TagName.ToLowerInvariant();
        var classTokens = (element.GetAttribute("class") ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
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

        if (tag == "footer" || classTokens.Any(c => c.Contains("footer", StringComparison.OrdinalIgnoreCase)))
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

        if (tag == "aside")
        {
            return (linkDensity > 0.5 ? BlockRole.RelatedLinks : BlockRole.Sidebar, 0.75);
        }

        // Fix 4: tighten Form classification so mobile-nav toggles and pure-button
        // forms don't get classified as Form. Only classify as Form when there is
        // at least one meaningful text-entry input or textarea.
        if (tag == "form")
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
            // form with no meaningful inputs: fall through to default classification
        }
        else if (element.QuerySelectorAll("input, textarea")
            .Count(input =>
            {
                if (input.TagName.Equals("textarea", StringComparison.OrdinalIgnoreCase))
                    return true;
                var type = (input.GetAttribute("type") ?? "text").ToLowerInvariant();
                return type is "text" or "email" or "search" or "tel" or "url"
                       or "number" or "password" or "date" or "datetime-local"
                       or "month" or "week" or "time";
            }) >= 2)
        {
            return (BlockRole.Form, 0.85);
        }

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
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / totalText;
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
        var classTokens = (element.GetAttribute("class") ?? "")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (classTokens.Any(c => hints.Any(h => c.Contains(h, StringComparison.OrdinalIgnoreCase))))
            return true;

        var id = element.GetAttribute("id");
        if (!string.IsNullOrEmpty(id) && hints.Any(h => id.Contains(h, StringComparison.OrdinalIgnoreCase)))
            return true;

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
}
