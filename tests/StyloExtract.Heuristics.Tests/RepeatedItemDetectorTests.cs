using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Unit tests for <see cref="RepeatedItemDetector"/> and its integration into
/// <see cref="HeuristicBlockClassifier"/>.
/// </summary>
public class RepeatedItemDetectorTests
{
    // Parse HTML and return the body element for the detector.
    private static IElement ParseBody(string html)
    {
        var parser = new HtmlParser();
        var doc = parser.ParseDocument(html);
        return doc.Body!;
    }

    // Run the full classify pipeline on HTML.
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

    private static string LongText(int length = 200) =>
        "This is a long content block with enough words to pass the minimum text threshold. "
        + new string('x', Math.Max(0, length - 85))
        + " end.";

    // --- RepeatedItemDetector unit tests ---

    [Fact]
    public void Detects_StackExchange_AnswerBlocks()
    {
        // 5 answer-cell divs with substantial text: should detect 1 group with 5 items.
        var answers = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<div class=\"answer-cell\"><p>Answer number {i}: {LongText(150)}</p></div>"));
        var html = $"<html><body><div id=\"answers\">{answers}</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().HaveCount(1, "five similar answer-cell divs inside a container is one group");
        groups[0].Items.Should().HaveCount(5);
    }

    [Fact]
    public void Detects_Discourse_Posts()
    {
        // 4 article.post elements - Discourse forum pattern.
        var posts = string.Concat(Enumerable.Range(1, 4).Select(i =>
            $"<article class=\"post\"><div class=\"post-body\">Post {i}: {LongText(160)}</div></article>"));
        var html = $"<html><body><div id=\"topic\">{posts}</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().HaveCount(1);
        groups[0].Items.Should().HaveCount(4);
    }

    [Fact]
    public void Detects_XenForo_Messages()
    {
        // 6 article.message elements - XenForo pattern.
        var messages = string.Concat(Enumerable.Range(1, 6).Select(i =>
            $"<article class=\"message\"><div class=\"messageBody\">Message {i}: {LongText(140)}</div></article>"));
        var html = $"<html><body><div class=\"p-body\">{messages}</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().HaveCount(1);
        groups[0].Items.Should().HaveCount(6);
    }

    [Fact]
    public void Skips_NavigationItems()
    {
        // ul > li with short text: navigation menu, not repeated content.
        var html = "<html><body><ul><li>Home</li><li>About</li><li>Contact</li><li>Blog</li></ul></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().BeEmpty("navigation items have short text and must not trigger the detector");
    }

    [Fact]
    public void Skips_TableRows()
    {
        // table with substantial text rows: table container is in the skip list.
        var rows = string.Concat(Enumerable.Range(1, 5).Select(i =>
            $"<tr><td>{LongText(120)}</td></tr>"));
        var html = $"<html><body><table><tbody>{rows}</tbody></table></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().BeEmpty("table containers are explicitly excluded from repeated-item detection");
    }

    [Fact]
    public void PrefersBiggestGroup()
    {
        // Outer container has 4 similar items; each item contains 3 inner similar items.
        // The outer group (4 items) should win; inner groups (3 items) should be suppressed.
        string innerContent(int outer, int inner) =>
            $"<div class=\"inner-card\">Inner card {outer}-{inner}: {LongText(110)}</div>";
        string outerItem(int i) =>
            $"<div class=\"outer-card\">{innerContent(i, 1)}{innerContent(i, 2)}{innerContent(i, 3)}<p>extra</p></div>";
        var html = $"<html><body><div id=\"wrap\">{outerItem(1)}{outerItem(2)}{outerItem(3)}{outerItem(4)}</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        // Only the outer 4-item group should be selected (inner groups are consumed by it).
        groups.Should().HaveCount(1, "the biggest non-overlapping group must win");
        groups[0].Items.Should().HaveCount(4, "outer group has 4 items");
    }

    [Fact]
    public void RequiresClassOverlap()
    {
        // 3 divs with completely different class names: no class overlap.
        var html = "<html><body><div id=\"container\">"
            + $"<div class=\"alpha-unique-name\">Content: {LongText(130)}</div>"
            + $"<div class=\"beta-unique-name\">Content: {LongText(130)}</div>"
            + $"<div class=\"gamma-unique-name\">Content: {LongText(130)}</div>"
            + "</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().BeEmpty("divs with completely different class names lack class overlap and must be skipped");
    }

    // --- Integration test: repeated items appear in classifier output ---

    [Fact]
    public void Forum_Thread_Emits_All_Posts_As_RepeatedItems()
    {
        // Small synthetic forum thread with 4 posts.
        string post(int i) =>
            $"<div class=\"post\"><p>Post {i}: {LongText(180)}</p></div>";
        var html = "<html><body>"
            + "<header><nav><a href=\"/\">Home</a></nav></header>"
            + "<main>"
            + "<h1>Forum Thread Title</h1>"
            + $"<div id=\"posts\">{post(1)}{post(2)}{post(3)}{post(4)}</div>"
            + "</main>"
            + "</body></html>";

        var blocks = Classify(html);

        var repeatedItems = blocks.Where(b => b.Role == BlockRole.RepeatedItem).ToList();
        repeatedItems.Should().HaveCount(4, "all 4 forum posts must be emitted as RepeatedItem blocks");

        // Verify they are in document order (Post 1 before Post 2 etc.)
        for (int i = 0; i < repeatedItems.Count; i++)
        {
            repeatedItems[i].Text.Should().Contain($"Post {i + 1}:",
                $"block at index {i} should contain Post {i + 1}");
        }
    }

    [Fact]
    public void RepeatedItems_Appear_In_Classifier_Output()
    {
        // RepeatedItem blocks must be produced by the classifier for forum-like pages.
        string post(int i) => $"<article class=\"post\"><p>Post {i}: {LongText(180)}</p></article>";
        var html = $"<html><body><div id=\"thread\">{post(1)}{post(2)}{post(3)}</div></body></html>";

        var blocks = Classify(html);

        blocks.Should().Contain(b => b.Role == BlockRole.RepeatedItem,
            "RepeatedItem blocks must be produced by the classifier for forum-like pages");
    }

    [Fact]
    public void Skips_FormFieldGrid_GravityFormsPattern()
    {
        // Gravity Forms / WPForms emit 5+ similar gfield divs inside a form. They look
        // like repeated items (same class, substantial label+description text) but are
        // input UI, not page content. Containers inside <form> must be skipped.
        var fields = string.Concat(Enumerable.Range(1, 6).Select(i =>
            $"<div class=\"gfield gfield--type-text\"><label>Question {i}</label>"
            + $"<div class=\"description\">{LongText(140)}</div><input type=\"text\"/></div>"));
        var html = $"<html><body><form id=\"contact\"><div class=\"gform_fields\">{fields}</div></form></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().BeEmpty("repeated form-field rows are input UI, not content items");
    }

    [Fact]
    public void Skips_StyleOnlyDivs_WithoutClassSignal()
    {
        // Some doc/service pages use bare <div style="max-width:Npx"> wrappers around
        // unrelated content. Three of them in a row should NOT count as items; without
        // any class signal we cannot conclude they form a typed group.
        var html = "<html><body><div id=\"wrap\">"
            + $"<div style=\"max-width:600px\">Block one: {LongText(140)}</div>"
            + $"<div style=\"max-width:600px\">Block two: {LongText(140)}</div>"
            + $"<div style=\"max-width:600px\">Block three: {LongText(140)}</div>"
            + "</div></body></html>";

        var body = ParseBody(html);
        var groups = RepeatedItemDetector.Detect(body);

        groups.Should().BeEmpty("style-only divs without class signal do not form a typed group");
    }

    [Fact]
    public void Article_Page_Not_Degraded_By_RepeatedItemDetector()
    {
        // A standard article page must NOT be changed: single MainContent block.
        var bodyText = "This is the main article body. " + LongText(600);
        var html = "<html><body>"
            + "<header><nav><a href=\"/\">Home</a><a href=\"/about\">About</a></nav></header>"
            + $"<main><article><h1>Article Title</h1><p>{bodyText}</p></article></main>"
            + "<footer>Copyright 2026</footer>"
            + "</body></html>";

        var blocks = Classify(html);

        blocks.Should().NotContain(b => b.Role == BlockRole.RepeatedItem,
            "a single article page must not trigger repeated-item detection");

        bool hasContent = blocks.Any(b =>
            b.Role == BlockRole.MainContent || b.Role == BlockRole.Article);
        hasContent.Should().BeTrue("the article body must still be classified as MainContent or Article");
    }
}
