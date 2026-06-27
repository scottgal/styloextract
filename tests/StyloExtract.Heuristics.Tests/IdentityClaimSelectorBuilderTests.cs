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
