using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Pins the news-card-listing recognition: a homepage of N article cards each
/// wrapped in a single anchor (image + headline + excerpt all inside one
/// &lt;a&gt;) must NOT be rejected as a navigation listing on link-density
/// grounds. The Register, Hacker News with images, Verge, Ars Technica, BBC
/// News landing, and any other site that follows the HTML5 spec for using
/// &lt;article&gt; per card all hit this pattern.
///
/// Before this fix the RepeatedItemDetector rejected groups where
/// avgLinkDensity &gt; 0.65, which is exactly the shape a whole-card-link
/// pattern produces. The result was a footer-only render — the only block
/// the page had with substantial non-link text.
///
/// Architectural rule (not a per-site rule): when items are HTML5 semantic
/// content tags (<c>&lt;article&gt;</c>), they ARE content by definition.
/// Skip the link-density rejection for them.
/// </summary>
public class NewsCardListRecognitionTests
{
    private static IReadOnlyList<ExtractedBlock> Classify(string html)
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);
        return classifier.Classify(segmenter.Segment(doc));
    }

    private static string ArticleCard(int i) =>
        $"<article class='column' itemscope>" +
            $"<a href='/article/{i}'>" +
                $"<figure><img src='/img/{i}.jpg' alt='Card {i} image' /></figure>" +
                $"<h4>Card {i} Headline About Something Interesting Happening Today</h4>" +
                $"<p>Card {i} excerpt body text that summarises the article in a sentence or two for the listing page.</p>" +
            $"</a>" +
        $"</article>";

    [Fact]
    public void Register_Shape_Many_Article_Cards_Each_A_Single_Link_Emits_Cards_Not_Just_Footer()
    {
        // 15 article cards (well above RepeatedItemDetector.MinChildren = 3),
        // each shaped like a Register / Verge / Ars news card: the entire
        // card is wrapped in a single <a>, link density per item ≈ 1.0.
        var cards = string.Concat(Enumerable.Range(0, 15).Select(ArticleCard));
        var html =
            "<html><body>" +
                "<header><nav><a href='/'>Home</a><a href='/news'>News</a></nav></header>" +
                $"<div class='listing'>{cards}</div>" +
                "<footer><a href='/about'>About</a><a href='/contact'>Contact</a><p>Copyright 2026</p></footer>" +
            "</body></html>";

        var blocks = Classify(html);

        // Each card must surface as a RepeatedItem block.
        var repeatedItems = blocks.Where(b => b.Role == BlockRole.RepeatedItem).ToList();
        repeatedItems.Should().HaveCountGreaterThan(5,
            "a homepage of 15 article cards should emit ~15 RepeatedItem blocks (not zero, not just the footer)");

        // Their combined text should contain the headlines and excerpts.
        var combinedText = string.Concat(repeatedItems.Select(b => b.Text));
        combinedText.Should().Contain("Card 0 Headline");
        combinedText.Should().Contain("Card 14 Headline");
    }

    [Fact]
    public void NavMenu_Pattern_Still_Rejected_Despite_The_Fix()
    {
        // Regression guard: a navigation menu (<li><a>text</a></li> style)
        // must STILL be rejected by the link-density check. Only <article>
        // gets the bypass. The fix is scoped to HTML5 semantic content
        // containers, not to "anything with high link density".
        var navItems = string.Concat(Enumerable.Range(0, 20).Select(i =>
            $"<li class='menu-item'><a href='/section/{i}'>Section number {i} navigation link entry here</a></li>"));
        var html =
            "<html><body>" +
                "<header><nav><ul class='menu'>" + navItems + "</ul></nav></header>" +
                "<main><h1>Article Body</h1><p>" + new string('x', 600) + "</p></main>" +
            "</body></html>";

        var blocks = Classify(html);

        // The 20 nav-list items must NOT have been promoted to RepeatedItem
        // content. MainContent should be the <main> body. The nav items get
        // classified under a Navigation role.
        var repeatedItems = blocks.Where(b => b.Role == BlockRole.RepeatedItem).ToList();
        repeatedItems.Should().BeEmpty(
            "the link-density rejection still applies to <li>-of-links nav menus");
    }
}