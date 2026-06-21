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
        const string html = "<html><body><div class='cookie-bar'><p>We use cookies to improve your experience.</p><button>Accept all cookies</button><a href='/policy'>Learn more</a></div></body></html>";
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

    // --- New tests for v1.2.1 selection behaviour ---

    [Fact]
    public void Classify_NestedWrappers_KeepsHighestScoringSubtreeOnly()
    {
        // A typical blog page: <main> wraps <article> wraps div wrappers.
        // The non-overlapping selection must emit exactly ONE block covering the
        // article body, not one block per ancestor wrapper.
        const string body = "This is the main article body text. It contains a thorough discussion of the topic at hand with substantial prose content to ensure correct classification by the heuristic classifier.";
        var html = $"<html><body><main><article><div class='wrapper'><div class='container'><p>{body}</p></div></div></article></main></body></html>";
        var (blocks, _) = Classify(html);

        blocks.Should().HaveCount(1, "nested wrappers must collapse to a single selected block");
        blocks[0].Role.Should().BeOneOf(BlockRole.MainContent, BlockRole.Article);
        blocks[0].Text.Should().Contain(body.Substring(0, 40));
    }

    [Fact]
    public void Classify_EmptyDivs_NotEmitted()
    {
        // Empty wrapper divs should produce zero blocks after quality-based selection.
        const string realContent = "This is the real article content with enough text to pass the quality gate and be selected by the non-overlapping subtree picker.";
        var html = $"<html><body><main><article><p>{realContent}</p></article></main><div><div></div></div></body></html>";
        var (blocks, _) = Classify(html);

        // The empty <div><div></div></div> chain must not appear in output.
        blocks.Should().NotContain(b => b.Text.Trim().Length == 0,
            "empty blocks carry no information and must be filtered out");
        blocks.Should().Contain(b => b.Text.Contains(realContent.Substring(0, 40)));
    }

    [Fact]
    public void Classify_MobileNavForm_NotClassifiedAsForm()
    {
        // A <form> with only a button and no meaningful text input must not be
        // classified as Form (mobile-nav toggle pattern).
        const string html = "<html><body><form><button type='button'>Menu</button></form><main><p>Content goes here with enough text to matter.</p></main></body></html>";
        var (blocks, _) = Classify(html);

        blocks.Should().NotContain(b => b.Role == BlockRole.Form,
            "a form with no meaningful text inputs is not a Form block");
    }

    [Fact]
    public void Classify_SearchForm_IsClassifiedAsForm()
    {
        // A <form> with a search input must be classified as Form.
        const string html = "<html><body><form><input type='search' name='q' /><button>Go</button></form></body></html>";
        var (blocks, _) = Classify(html);

        blocks.Should().Contain(b => b.Role == BlockRole.Form,
            "a form with a search input is a meaningful Form block");
    }
}
