using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorInducerTests
{
    [Fact]
    public void Induce_ProducesOneRulePerRoleCssPair()
    {
        IExtractorInducer inducer = new ExtractorInducer();
        var blocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "", Markdown = "", XPath = "/html/body/main/article", CssSelector = "main > article", TextLength = 500, LinkDensity = 0.05, Links = Array.Empty<ExtractedLink>() },
            new ExtractedBlock { Id = "b1", Role = BlockRole.PrimaryNavigation, Confidence = 0.95, Text = "", Markdown = "", XPath = "/html/body/header/nav", CssSelector = "header > nav", TextLength = 50, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var id = Guid.NewGuid();
        var extractor = inducer.Induce(id, blocks);

        extractor.TemplateId.Should().Be(id);
        extractor.Version.Should().Be(1);
        extractor.Rules.Should().HaveCount(2);
        extractor.Rules.Select(r => r.Role).Should().BeEquivalentTo(new[] { BlockRole.MainContent, BlockRole.PrimaryNavigation });
        extractor.Centroid.TotalObservations.Should().Be(1);
    }

    [Fact]
    public void Induce_RepeatedRoleBlocks_CollapseToSingleRuleWithSharedChain()
    {
        // Three RepeatedItem blocks on a page with a real document → the
        // cardinality-aware dispatch routes through BuildForRepeatedRole and
        // emits ONE shared chain that matches all three. They must collapse
        // into a single rule (same CSS string).
        var html = """
            <!DOCTYPE html>
            <html><body><main>
                <article class="post-card">a</article>
                <article class="post-card">b</article>
                <article class="post-card">c</article>
            </main></body></html>
            """;
        var doc = new HtmlParser().ParseDocument(html);
        var blocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.RepeatedItem, Confidence = 0.9, Text = "a", Markdown = "a", XPath = "/html[1]/body[1]/main[1]/article[1]", TextLength = 1, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() },
            new ExtractedBlock { Id = "b1", Role = BlockRole.RepeatedItem, Confidence = 0.9, Text = "b", Markdown = "b", XPath = "/html[1]/body[1]/main[1]/article[2]", TextLength = 1, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() },
            new ExtractedBlock { Id = "b2", Role = BlockRole.RepeatedItem, Confidence = 0.9, Text = "c", Markdown = "c", XPath = "/html[1]/body[1]/main[1]/article[3]", TextLength = 1, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() },
        };

        IExtractorInducer inducer = new ExtractorInducer();
        var extractor = inducer.Induce(Guid.NewGuid(), blocks, doc);

        var repeated = extractor.Rules.Where(r => r.Role == BlockRole.RepeatedItem).ToList();
        repeated.Should().HaveCount(1,
            "all three RepeatedItem blocks must collapse to one rule with a shared chain");
        repeated[0].Claims.Should().NotBeNull();
        repeated[0].Claims!.Last().Classes.Should().Contain("post-card");
    }

    [Fact]
    public void Induce_SingletonRoleDispatch_StaysOnSingleTargetPath()
    {
        // Sanity: a singleton role (MainContent) still flows through the
        // single-target builder — one rule, one chain, unchanged from Task 51.
        var html = """
            <!DOCTYPE html>
            <html><body><main id="main"><article><p>x</p></article></main></body></html>
            """;
        var doc = new HtmlParser().ParseDocument(html);
        var blocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "x", Markdown = "x", XPath = "/html[1]/body[1]/main[1]", TextLength = 1, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() },
        };

        IExtractorInducer inducer = new ExtractorInducer();
        var extractor = inducer.Induce(Guid.NewGuid(), blocks, doc);

        var rule = extractor.Rules.Single();
        rule.Role.Should().Be(BlockRole.MainContent);
        rule.Claims.Should().NotBeNull();
        rule.Claims![0].Tag.Should().Be("main");
    }
}
