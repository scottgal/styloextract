using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class IdentityClaimSelectorBuilderTests
{
    private static readonly IClassStabilityFilter Filter = new DefaultClassStabilityFilter();

    private static (IDocument Doc, IElement Target) Parse(string html, string selector)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var target = doc.QuerySelector(selector)!;
        target.Should().NotBeNull("test fixture must contain " + selector);
        return (doc, target);
    }

    [Fact]
    public void BuildAncestorChain_UniqueId_ReturnsSingleClaim()
    {
        var (doc, target) = Parse(
            "<html><body><main><article id='post-1'><p>x</p></article></main></body></html>",
            "#post-1");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse();
        result.Chain.Should().HaveCount(1);
        result.Chain[0].Tag.Should().Be("article");
        result.Chain[0].Id.Should().Be("post-1");
        result.Chain[0].Classes.Should().BeEmpty();
    }

    [Fact]
    public void BuildAncestorChain_UniqueClass_ReturnsSingleClassClaim()
    {
        var (doc, target) = Parse(
            "<html><body><main><article class='article-body'><p>x</p></article></main></body></html>",
            "article.article-body");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse();
        result.Chain.Should().HaveCount(1);
        result.Chain[0].Tag.Should().Be("article");
        result.Chain[0].Classes.Should().BeEquivalentTo(["article-body"]);
    }

    [Fact]
    public void BuildAncestorChain_TargetNeedsParentAnchor_ProducesTwoClaimChain()
    {
        // The <h1> tag is plain and ambiguous; two <h1>s exist. Only the parent
        // is identity-rich (id='main'). Builder should walk up one and emit
        // a 2-claim chain.
        var (doc, target) = Parse(
            "<html><body>" +
            "<header><h1>Site Title</h1></header>" +
            "<main id='main'><h1>Article Title</h1></main>" +
            "</body></html>",
            "#main h1");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse();
        result.Chain.Should().HaveCount(2);
        result.Chain[0].Tag.Should().Be("main");
        result.Chain[0].Id.Should().Be("main");
        result.Chain[1].Tag.Should().Be("h1");
    }

    [Fact]
    public void BuildAncestorChain_NoIdentityAnywhere_BuildsTagChainUpToDepthCap()
    {
        // Pure-tag document with multiple <p> nodes - no chain length will ever
        // make the target unique because everything is tag-only and parents are
        // duplicated too. Builder must stop at the cap and signal HitDepthCap.
        var (doc, target) = Parse(
            "<html><body>" +
            "<div><div><div><div><div><p>a</p></div></div></div></div></div>" +
            "<div><div><div><div><div><p>b</p></div></div></div></div></div>" +
            "</body></html>",
            "p"); // first p

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeTrue();
        result.Chain.Length.Should().BeLessThanOrEqualTo(IdentityClaimSelectorBuilder.MaxChainDepth + 1);
        result.Chain.All(c => c.Id is null && c.Classes.Count == 0).Should().BeTrue();
    }

    [Fact]
    public void BuildAncestorChain_PicksMostSpecificClassByDocumentFrequency()
    {
        // Two stable class candidates on the target. "article-body" appears
        // many times in the doc; "rare-anchor" appears only once. Builder must
        // pick "rare-anchor" (lowest doc frequency).
        var html =
            "<html><body>" +
            "<div class='article-body'>generic</div>" +
            "<div class='article-body'>generic</div>" +
            "<div class='article-body'>generic</div>" +
            "<main class='article-body rare-anchor'><p>target</p></main>" +
            "</body></html>";
        var (doc, target) = Parse(html, "main");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.Chain.Should().HaveCount(1);
        result.Chain[0].Classes.Should().BeEquivalentTo(["rare-anchor"]);
    }

    [Fact]
    public void BuildAncestorChain_HashShapedClassesRejectedByFilter()
    {
        // class="article-body css-1a2b3c4" - the hash-shaped token MUST be
        // dropped by the stability filter so it never appears in the emitted
        // claim. Verifies the filter is honoured at the call site.
        var (doc, target) = Parse(
            "<html><body><article class='article-body css-1a2b3c4'></article></body></html>",
            "article");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.Chain.Should().HaveCount(1);
        result.Chain[0].Classes.Should().BeEquivalentTo(["article-body"]);
        result.Chain[0].Classes.Should().NotContain("css-1a2b3c4");
    }

    // ---- Task 51 / 2.1: hard uniqueness postcondition ----

    /// <summary>
    /// Walk an emitted chain back over the document: at each candidate leaf
    /// element of the chain's leaf tag, verify each ancestor (one strict
    /// parent at a time, matching the '>' combinator the builder uses) matches
    /// the corresponding chain entry. Returns the matching candidate elements.
    /// </summary>
    private static IReadOnlyList<IElement> ApplyChain(IDocument doc, IReadOnlyList<IdentityClaim> chain)
    {
        if (chain.Count == 0) return Array.Empty<IElement>();
        var leaf = chain[^1];
        var results = new List<IElement>();
        foreach (var el in doc.GetElementsByTagName(leaf.Tag))
        {
            // Pass-through filter: chain claims already encode stability-filtered
            // class sets; re-filtering here would double-reject.
            var set = IdentityClaimExtractor.Extract(el, new PassThroughFilter());
            if (!IdentityClaimMatcher.Matches(set, leaf)) continue;
            if (!AncestorsMatchChain(el, chain)) continue;
            results.Add(el);
        }
        return results;
    }

    private static bool AncestorsMatchChain(IElement leafElement, IReadOnlyList<IdentityClaim> chain)
    {
        if (chain.Count <= 1) return true;
        var parent = leafElement.ParentElement;
        for (var i = chain.Count - 2; i >= 0; i--)
        {
            if (parent is null) return false;
            var set = IdentityClaimExtractor.Extract(parent, new PassThroughFilter());
            if (!IdentityClaimMatcher.Matches(set, chain[i])) return false;
            parent = parent.ParentElement;
        }
        return true;
    }

    private sealed class PassThroughFilter : IClassStabilityFilter
    {
        public bool IsStable(string token) => true;
    }

    [Fact]
    public void EmittedChain_AlwaysMatchesExactlyOneElement_OnInductionDocument()
    {
        // Multiple <ul> elements, some sharing the Tailwind utility class sm:gap-2.
        // The target carries an id; the filter must reject sm:gap-2 so the builder
        // picks the id directly. The emitted chain must match exactly one element.
        var html = """
            <!DOCTYPE html>
            <html><body>
                <header><nav class="sm:gap-2"><ul class="sm:gap-2"><li>n1</li></ul></nav></header>
                <main>
                    <ul class="sm:gap-2"><li>item</li></ul>
                    <ul class="post-list sm:gap-2" id="post-list">
                        <li>real target</li>
                    </ul>
                </main>
            </body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var target = doc.QuerySelector("#post-list")!;
        target.Should().NotBeNull("fixture must contain #post-list");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse("an id-anchored target must reach uniqueness");
        var matches = ApplyChain(doc, result.Chain);
        matches.Should().HaveCount(1, "the emitted chain must match exactly one element");
        matches.Single().Should().BeSameAs(target, "the matched element must be the original target");

        // Sanity: no claim in the chain may contain a Tailwind utility class.
        foreach (var claim in result.Chain)
        {
            claim.Classes.Should().NotContain(c => c.Contains(':'),
                "no claim may contain a Tailwind variant class (sm:/md:/etc.)");
        }
    }

    [Fact]
    public void NoIdNoUniqueClass_WalksAncestorsToUniqueAnchor()
    {
        // No id, no unique class on the target <p>. Tag alone matches 3 elements.
        // Builder must walk ancestors until the chain is unique on the doc.
        var html = """
            <!DOCTYPE html>
            <html><body>
                <header><div class="banner"><p>a</p></div></header>
                <main><div class="story"><p>b</p></div></main>
                <footer><div class="legal"><p>c</p></div></footer>
            </body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var target = doc.QuerySelector("main p")!;
        target.Should().NotBeNull("fixture must contain main p");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse("walking ancestors must reach a unique anchor");
        var matches = ApplyChain(doc, result.Chain);
        matches.Should().HaveCount(1, "the chain must be unique on the document");
        matches.Single().Should().BeSameAs(target, "the matched element must be the original target");
        result.Chain.Length.Should().BeGreaterOrEqualTo(2,
            "the chain must include at least one ancestor to disambiguate");
    }

    [Fact]
    public void DeepUtilityClassSoup_StillReachesUniqueness_WithExtendedCap()
    {
        // 6 deep ancestors all carrying Tailwind utilities (filtered out) wrap a
        // target that has only an id. With MaxChainDepth=8 the walk must still
        // reach the id-bearing ancestor and become unique.
        var html = """
            <!DOCTYPE html>
            <html><body>
                <div class="flex"><div class="grid"><div class="p-4"><div class="m-2"><div class="bg-white"><div class="rounded"><article id="post-42"><p>target</p></article></div></div></div></div></div></div>
                <div class="flex"><div class="grid"><div class="p-4"><div class="m-2"><div class="bg-white"><div class="rounded"><article><p>distractor</p></article></div></div></div></div></div></div>
            </body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var target = doc.QuerySelector("#post-42 p")!;
        target.Should().NotBeNull("fixture must contain #post-42 p");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);

        result.HitDepthCap.Should().BeFalse("the id-bearing ancestor is reachable within MaxChainDepth=8");
        var matches = ApplyChain(doc, result.Chain);
        matches.Should().HaveCount(1, "uniqueness must hold");
        matches.Single().Should().BeSameAs(target);
    }

    [Fact]
    public void MostlyLucidStyle_PrimaryNavUl_DoesNotEmitTailwindUtility()
    {
        // Regression: in the alpha.19 mostlylucid.net smoke, the PrimaryNavigation
        // anchor came out as `ul.sm:gap-2` — the stability filter accepted the
        // Tailwind responsive utility. Now sm:gap-2 must be filtered, and the
        // builder must walk up to the header's #header id to anchor the chain.
        var html = """
            <!DOCTYPE html>
            <html><body>
                <header class="sticky top-0 z-40" id="header">
                    <ul class="flex items-center w-full gap-1 sm:gap-2">
                        <li>home</li>
                        <li>about</li>
                    </ul>
                </header>
                <main>
                    <ul class="flex gap-1 sm:gap-2"><li>filter chip</li></ul>
                </main>
            </body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var headerNavUl = doc.QuerySelector("header > ul")!;
        headerNavUl.Should().NotBeNull("fixture must contain header > ul");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(headerNavUl, doc, Filter);

        result.HitDepthCap.Should().BeFalse();

        // Smoke: NO claim in the emitted chain may contain a Tailwind variant
        // class. Variant classes contain ':'. Utility classes go through the
        // prefix filter — assert by sampling several known utilities.
        var bannedUtilities = new[] { "sm:gap-2", "gap-1", "flex", "items-center", "w-full", "sticky", "top-0", "z-40" };
        foreach (var claim in result.Chain)
        {
            foreach (var u in bannedUtilities)
            {
                claim.Classes.Should().NotContain(u,
                    $"claim must not anchor on Tailwind utility '{u}'");
            }
        }

        // The chain must match exactly one element on the doc.
        var matches = ApplyChain(doc, result.Chain);
        matches.Should().HaveCount(1);
        matches.Single().Should().BeSameAs(headerNavUl);
    }

    [Fact]
    public void NonUniqueAtMaxDepth_SetsHitDepthCap()
    {
        // Two identical 9-deep div trees with an inner <p>. No ids, no useful
        // classes. The chain can never become unique because the structure is
        // duplicated. Builder must hit the depth cap and emit HitDepthCap=true.
        var html = """
            <!DOCTYPE html>
            <html><body>
                <div><div><div><div><div><div><div><div><div><p>a</p></div></div></div></div></div></div></div></div></div>
                <div><div><div><div><div><div><div><div><div><p>b</p></div></div></div></div></div></div></div></div></div>
            </body></html>
            """;
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        var firstP = doc.QuerySelectorAll("p").First();

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(firstP, doc, Filter);

        result.HitDepthCap.Should().BeTrue(
            "structurally-duplicated tag-only chains can never be unique; the cap must be hit");
        result.Chain.Length.Should().BeLessThanOrEqualTo(IdentityClaimSelectorBuilder.MaxChainDepth + 1);
    }

    [Fact]
    public void ToCssSelector_RendersIdAndClassesAndDataAttrs()
    {
        // Direct render check - verifies the back-compat CSS string is what
        // the YAML side-files will start showing.
        var html =
            "<html><body>" +
            "<div id='page'>" +
            "<main class='article-body'>" +
            "<article data-post='42'>x</article>" +
            "</main></div></body></html>";
        var (doc, target) = Parse(html, "article");

        var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, doc, Filter);
        var css = IdentityClaimSelectorBuilder.ToCssSelector(result.Chain);

        css.Should().Contain("article");
        // At least one of the rendered identity affixes must be present
        // (#page, .article-body, [data-post]).
        (css.Contains("#") || css.Contains(".") || css.Contains("[data-")).Should().BeTrue();
        // No positional indexes should leak in.
        css.Should().NotContain("nth-of-type");
        css.Should().NotContain("nth-child");
    }
}
