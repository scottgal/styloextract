using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class HeuristicBlockClassifierTests
{
    private static (IReadOnlyList<ExtractedBlock> Blocks, IDocument Doc) Classify(string html)
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);
        var blocks = classifier.Classify(segmenter.Segment(doc));
        return (blocks, doc);
    }

    [Fact]
    public void Classify_Nav_AsPrimaryNavigation()
    {
        const string html = "<html><body><header><nav class='main-menu'><a href='/'>H</a><a href='/a'>A</a><a href='/b'>B</a><a href='/c'>C</a></nav></header></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().ContainSingle(b => b.Role == BlockRole.PrimaryNavigation);
    }

    [Fact]
    public void Classify_Article_AsMainContent()
    {
        var html = "<html><body><main><article><h1>Title</h1><p>" + new string('x', 400) + "</p></article></main></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
    }

    [Fact]
    public void Classify_Footer_AsFooter()
    {
        const string html = "<html><body><footer>© 2026 Acme. All rights reserved.</footer></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Footer);
    }

    [Fact]
    public void Classify_CookieBanner_AsCookieBanner()
    {
        const string html = "<html><body><div class='cookie-bar'>We use cookies <button>Accept all cookies</button></div></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.CookieBanner);
    }

    [Fact]
    public void Classify_AdDiv_AsAdvertisement()
    {
        const string html = "<html><body><div class='ad sponsored'><a href='x'>1</a><a href='y'>2</a><a href='z'>3</a></div></body></html>";
        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Advertisement);
    }
}
