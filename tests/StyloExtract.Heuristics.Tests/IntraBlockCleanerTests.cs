using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Regression tests for the v1.3 intra-block cleaning pass. Tests operate at the
/// HeuristicBlockClassifier level so they validate end-to-end behaviour including
/// the point in the pipeline where IntraBlockCleaner is invoked.
/// </summary>
public class IntraBlockCleanerTests
{
    private static IReadOnlyList<ExtractedBlock> Classify(string html)
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        IBlockSegmenter segmenter = new BlockSegmenter();
        IBlockClassifier classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        var doc = parser.Parse(html);
        cleaner.Clean(doc);
        return classifier.Classify(segmenter.Segment(doc));
    }

    [Fact]
    public void IntraBlockCleaner_StripsTocFromMainContent()
    {
        // A <main> element that contains an internal TOC <nav> followed by the real article.
        // After cleaning the TOC section headings must not appear in the block text.
        const string html = @"<html><body>
<main>
  <nav class=""toc"">
    <a href=""#a"">Section A</a>
    <a href=""#b"">Section B</a>
  </nav>
  <h1>Article title</h1>
  <p>Article body paragraph one with substantial content about the topic being discussed.</p>
  <p>Article body paragraph two covers additional aspects of the subject matter in detail.</p>
</main>
</body></html>";

        var blocks = Classify(html);

        var main = blocks.FirstOrDefault(b => b.Role is BlockRole.MainContent or BlockRole.Article);
        main.Should().NotBeNull("a main content block must be selected");
        main!.Text.Should().NotContain("Section A", "TOC entries must be stripped from the main block");
        main.Text.Should().NotContain("Section B", "TOC entries must be stripped from the main block");
        main.Text.Should().Contain("Article title", "the article heading must be preserved");
        main.Text.Should().Contain("Article body paragraph", "the article body must be preserved");
    }

    [Fact]
    public void IntraBlockCleaner_StripsToolbarInsideMain()
    {
        // A <main> element that contains an action-bar toolbar before the article heading.
        // After cleaning the toolbar button labels must not appear in the block text.
        const string html = @"<html><body>
<main>
  <div class=""action-bar"">
    <button>Edit</button>
    <button>Print</button>
    <a href=""/share"">Share</a>
  </div>
  <h1>Title</h1>
  <p>Body content with enough text to pass the quality gate and be selected as main content.</p>
</main>
</body></html>";

        var blocks = Classify(html);

        var main = blocks.FirstOrDefault(b => b.Role is BlockRole.MainContent or BlockRole.Article);
        main.Should().NotBeNull("a main content block must be selected");
        main!.Text.Should().NotContain("Edit", "toolbar buttons must be stripped from the main block");
        main.Text.Should().NotContain("Print", "toolbar buttons must be stripped from the main block");
        main.Text.Should().NotContain("Share", "toolbar links must be stripped from the main block");
        main.Text.Should().Contain("Title", "the article heading must be preserved");
        main.Text.Should().Contain("Body content", "the article body must be preserved");
    }

    [Fact]
    public void IntraBlockCleaner_StripsBreadcrumbInsideArticle()
    {
        // An <article> element that contains a breadcrumb <nav> before the article body.
        // After cleaning the breadcrumb trail must not appear in the block text.
        const string html = @"<html><body>
<article>
  <nav class=""breadcrumb""><a href=""/"">Home</a> &gt; <a href=""/section"">Section</a></nav>
  <h1>Title</h1>
  <p>Body content with enough text to be classified correctly as article content by the heuristic.</p>
</article>
</body></html>";

        var blocks = Classify(html);

        var main = blocks.FirstOrDefault(b => b.Role is BlockRole.MainContent or BlockRole.Article);
        main.Should().NotBeNull("an article block must be selected");
        main!.Text.Should().NotContain("Home", "breadcrumb trail must be stripped from the article block");
        main.Text.Should().NotContain("Section", "breadcrumb trail must be stripped from the article block");
        main.Text.Should().Contain("Title", "the article heading must be preserved");
        main.Text.Should().Contain("Body content", "the article body must be preserved");
    }

    [Fact]
    public void IntraBlockCleaner_PreservesCleanContent()
    {
        // A <main> with no contaminating descendants. The cleaner must not remove any
        // real content: headings, paragraphs, inline links, and tables must all survive.
        const string html = @"<html><body>
<main>
  <h1>Title</h1>
  <p>Paragraph one.</p>
  <p>Paragraph two with a <a href=""/x"">link</a> inside.</p>
  <table><tr><td>A</td><td>B</td></tr></table>
</main>
</body></html>";

        var blocks = Classify(html);

        var main = blocks.FirstOrDefault(b => b.Role is BlockRole.MainContent or BlockRole.Article);
        main.Should().NotBeNull("a main content block must be selected");
        main!.Text.Should().Contain("Title", "heading must be preserved");
        main.Text.Should().Contain("Paragraph one.", "first paragraph must be preserved");
        main.Text.Should().Contain("Paragraph two", "second paragraph must be preserved");
        main.Text.Should().Contain("A", "table cell A must be preserved");
        main.Text.Should().Contain("B", "table cell B must be preserved");
    }

    [Fact]
    public void IntraBlockCleaner_RecursivelyRemovesEmptyWrappers()
    {
        // A <main> that has a deeply nested TOC inside wrapper divs. After the TOC
        // is stripped the wrapper divs become empty and must also be removed.
        // The only surviving content must be the paragraph below the wrappers.
        const string html = @"<html><body>
<main>
  <div class=""wrapper"">
    <div class=""inner"">
      <nav class=""toc""><a href=""#x"">x</a></nav>
    </div>
  </div>
  <p>Real content that must survive the cleaning pass intact.</p>
</main>
</body></html>";

        var blocks = Classify(html);

        var main = blocks.FirstOrDefault(b => b.Role is BlockRole.MainContent or BlockRole.Article);
        main.Should().NotBeNull("a main content block must be selected");
        main!.Text.Should().NotContain("x", "the TOC link text must be stripped");
        main.Text.Should().Contain("Real content", "the paragraph must be preserved");
        // The wrapper and inner divs were also removed as empty wrappers; verify the
        // block text does not contain any residual noise from the nav element.
        main.Text.Trim().Should().StartWith("Real content",
            "after stripping the empty wrappers the first content must be the paragraph");
    }
}
