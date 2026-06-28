using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Pins detection of "image-anchor picker" containers: a div whose direct
/// children are N+ elements that each wrap a single &lt;a&gt; with empty or
/// near-empty link text. Catches language-flag pickers, social-share button
/// rows, follow-us icon strips, and "available on" platform-badge rows.
///
/// Architectural rule (not per-site): when ≥4 sibling children each contain
/// exactly one &lt;a&gt; whose TextContent is ≤4 chars, the wrapping div is
/// chrome, not content. Classify as Boilerplate so it doesn't get promoted
/// into MainContent.
/// </summary>
public class ImageAnchorPickerTests
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

    [Fact]
    public void Language_Flag_Picker_Is_Classified_As_Boilerplate_Not_Content()
    {
        // Models mostlylucid's leak: each language is <a hx-get><img/></a> with
        // no link text. 12 of these as siblings under a flex container.
        var flagAnchors = string.Concat(Enumerable.Range(0, 12).Select(i =>
            $"<div class='tooltip' data-tip='Lang{i}'>" +
                $"<a hx-get='/blog/lang{i}/slug' hx-target='#contentcontainer'>" +
                    $"<img src='/flag{i}.svg' alt='Language {i}' />" +
                "</a>" +
            "</div>"));

        var prose = string.Concat(Enumerable.Repeat(
            "This is a substantial article paragraph with real prose content. ",
            30));

        var html = "<html><body>" +
            "<div id='contentcontainer'>" +
                "<div id='blogpost'>" +
                    $"<div class='hidden sm:inline-flex'>{flagAnchors}</div>" +
                    $"<div class='prose'><h1>Article Title</h1><p>{prose}</p></div>" +
                "</div>" +
            "</div>" +
            "</body></html>";

        var blocks = Classify(html);

        var contentText = string.Concat(blocks
            .Where(b => b.Role is BlockRole.MainContent or BlockRole.Article)
            .Select(b => b.Text));
        contentText.Should().Contain("substantial article paragraph",
            "the actual article body should still be in MainContent");
        contentText.Should().NotContain("Language 11",
            "the 12-flag image-anchor picker must be classified as Boilerplate, " +
            "not bundled into MainContent");
    }

    [Fact]
    public void Social_Share_Row_Is_Classified_As_Boilerplate()
    {
        // Twitter / Facebook / LinkedIn / Reddit share buttons. Each is
        // <a href='...'>X</a> (one-char label or img-only).
        var shareButtons = string.Concat(new[] { "T", "F", "L", "R", "P", "E" }
            .Select(c => $"<div class='share-btn'><a href='#share-{c}'>{c}</a></div>"));

        var prose = string.Concat(Enumerable.Repeat(
            "This is a substantial article paragraph with real prose content. ",
            30));

        var html = "<html><body>" +
            "<main>" +
                "<h1>Article Title</h1>" +
                $"<div class='social-share'>{shareButtons}</div>" +
                $"<div class='article-body'><p>{prose}</p></div>" +
            "</main>" +
            "</body></html>";

        var blocks = Classify(html);

        var combinedText = string.Concat(blocks.Select(b => b.Text));
        combinedText.Should().Contain("substantial article paragraph");

        var mainContent = blocks
            .Where(b => b.Role is BlockRole.MainContent or BlockRole.Article)
            .ToList();
        // The single-letter share-button labels must not bleed into MainContent.
        var mainContentJoined = string.Concat(mainContent.Select(b => b.Text));
        // Hardest case: the share buttons emit no recognisable token. Assert
        // that the share-button anchors aren't surfaced as a separate Content
        // block either.
        blocks.Should().NotContain(b =>
            (b.Role == BlockRole.MainContent || b.Role == BlockRole.Article) &&
            b.Text.Contains("TFLRPE", StringComparison.Ordinal));
    }

    [Fact]
    public void Article_With_Inline_Links_Is_Not_Picker_Pattern()
    {
        // Regression guard: an article paragraph with inline anchors should
        // NOT be reclassified as a picker. The picker rule only matches the
        // structural shape "div with N+ direct children each containing
        // exactly one short-text anchor". A regular `<p>` with inline links
        // has links AS SIBLINGS of text inside the paragraph, not as
        // top-level items of a wrapper div.
        var html = "<html><body>" +
            "<main>" +
                "<h1>Article With Inline Links</h1>" +
                "<p>" +
                    "This article body discusses something interesting and links to " +
                    "<a href='/ref/0'>the first substantial reference</a>, " +
                    "<a href='/ref/1'>the second substantial reference</a>, " +
                    "<a href='/ref/2'>the third substantial reference</a>, and " +
                    "<a href='/ref/3'>the fourth substantial reference</a>. " +
                    "The article continues with more prose. " +
                    "The article continues with more prose. " +
                    "The article continues with more prose. " +
                    "The article continues with more prose." +
                "</p>" +
            "</main>" +
            "</body></html>";

        var blocks = Classify(html);

        var combinedText = string.Concat(blocks.Select(b => b.Text));
        combinedText.Should().Contain("first substantial reference",
            "inline article links must not be misclassified as picker chrome");
    }
}