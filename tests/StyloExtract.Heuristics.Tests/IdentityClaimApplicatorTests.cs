using System.IO.Hashing;
using System.Text;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Direct unit tests for the identity-claim apply path. The applicator is
/// stateless and document-agnostic, so the tests build minimal HTML fixtures
/// inline and assert against the matched IElement set.
/// </summary>
public class IdentityClaimApplicatorTests
{
    private static readonly IClassStabilityFilter Filter = new DefaultClassStabilityFilter();

    private static IDocument Parse(string html) => new HtmlParser().ParseDocument(html);

    private static ulong H(string s) => XxHash3.HashToUInt64(Encoding.UTF8.GetBytes(s));

    private static IdentityClaim TagOnly(string tag) =>
        new() { Tag = tag, TagHash = H(tag) };

    private static IdentityClaim TagId(string tag, string id) => new()
    {
        Tag = tag, TagHash = H(tag), Id = id, IdHash = H(id),
    };

    private static IdentityClaim TagClasses(string tag, params string[] classes)
    {
        var hashes = new ulong[classes.Length];
        for (var i = 0; i < classes.Length; i++) hashes[i] = H(classes[i]);
        return new IdentityClaim { Tag = tag, TagHash = H(tag), Classes = classes, ClassHashes = hashes };
    }

    [Fact]
    public void Apply_OneClaimChain_FindsLeafByTag()
    {
        var doc = Parse("<html><body><main>x</main></body></html>");

        var matches = IdentityClaimApplicator.Apply(new[] { TagOnly("main") }, doc, Filter);

        matches.Should().HaveCount(1);
        matches[0].LocalName.Should().Be("main");
    }

    [Fact]
    public void Apply_TwoClaimChain_NarrowsByAncestor()
    {
        // Two h1s — only the one under main#content should match the chain
        // [{tag: main, id: content}, {tag: h1}].
        var doc = Parse(
            "<html><body>" +
            "<header><h1>Site</h1></header>" +
            "<main id='content'><h1>Page</h1></main>" +
            "</body></html>");

        var chain = new[] { TagId("main", "content"), TagOnly("h1") };
        var matches = IdentityClaimApplicator.Apply(chain, doc, Filter);

        matches.Should().HaveCount(1);
        matches[0].TextContent.Should().Be("Page");
    }

    [Fact]
    public void Apply_ChainWithNoMatch_ReturnsEmpty()
    {
        var doc = Parse("<html><body><article>x</article></body></html>");

        var matches = IdentityClaimApplicator.Apply(new[] { TagOnly("main") }, doc, Filter);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ClassRequirement_OnlyMatchesElementsWithThatClass()
    {
        var doc = Parse(
            "<html><body>" +
            "<article class='post-card primary'>A</article>" +
            "<article class='related'>B</article>" +
            "<article class='post-card secondary'>C</article>" +
            "</body></html>");

        var matches = IdentityClaimApplicator.Apply(
            new[] { TagClasses("article", "post-card") }, doc, Filter);

        matches.Should().HaveCount(2);
        matches.Select(m => m.TextContent).Should().BeEquivalentTo(new[] { "A", "C" });
    }

    [Fact]
    public void Apply_EmptyChain_ReturnsEmpty()
    {
        var doc = Parse("<html><body><main>x</main></body></html>");

        var matches = IdentityClaimApplicator.Apply(Array.Empty<IdentityClaim>(), doc, Filter);

        matches.Should().BeEmpty();
    }

    [Fact]
    public void Apply_ExtraNonAnchorClassesOnElement_DoNotBreakMatch()
    {
        // The stability filter strips Tailwind-shaped utility classes at both
        // induction AND apply time. The claim only encodes the anchor class;
        // utility soup around it on the live element must not perturb matching.
        var doc = Parse(
            "<html><body>" +
            "<article class='post-card flex p-4 gap-2 mb-3'>A</article>" +
            "</body></html>");

        var matches = IdentityClaimApplicator.Apply(
            new[] { TagClasses("article", "post-card") }, doc, Filter);

        matches.Should().HaveCount(1);
    }

    [Fact]
    public void Apply_InduceAndApply_WithSameFilter_RoundTripsDeterministically()
    {
        // End-to-end determinism: extract a target's claim via the same
        // pipeline the inducer uses, then apply that claim back via the
        // applicator. Match must return the same element. This is the
        // contract that keeps induction and apply in lockstep — same
        // filter, same emission, same evaluation.
        var doc = Parse(
            "<html><body>" +
            "<main id='content'>" +
            "<section class='primary-block'><p>Target</p></section>" +
            "</main></body></html>");

        var target = doc.QuerySelector("section.primary-block")!;
        var leafSet = IdentityClaimExtractor.Extract(target, Filter);

        // Build a minimal one-claim chain from the leaf set.
        var leafClaim = new IdentityClaim
        {
            Tag = leafSet.Tag,
            TagHash = leafSet.TagHash,
            Classes = leafSet.Classes,
            ClassHashes = leafSet.ClassHashes,
        };

        var matches = IdentityClaimApplicator.Apply(new[] { leafClaim }, doc, Filter);

        matches.Should().HaveCount(1);
        matches[0].Should().BeSameAs(target);
    }
}
