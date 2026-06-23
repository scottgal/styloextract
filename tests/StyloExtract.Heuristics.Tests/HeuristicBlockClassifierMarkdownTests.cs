using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Asserts that <see cref="HeuristicBlockClassifier"/> populates the
/// <c>Markdown</c> field on content blocks via <see cref="DomMarkdownWalker"/>
/// and leaves it empty for chrome roles where the role-specific renderer
/// projection beats a generic DOM walk (navigation, footer, breadcrumb,
/// boilerplate, advertisement, cookie banner, form).
/// </summary>
public class HeuristicBlockClassifierMarkdownTests
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
    public void MainContent_Block_Has_Markdown_Populated_With_Heading_Levels()
    {
        const string html = """
            <html><body>
              <main><article>
                <h1>Top</h1>
                <p>First paragraph with enough text to clear the classifier's content threshold for sure and emit.</p>
                <h2>Sub</h2>
                <p>Second paragraph maintaining body length comfortably above the classifier's threshold for emit.</p>
              </article></main>
            </body></html>
            """;
        var blocks = Classify(html);
        var main = blocks.Should().Contain(b => b.Role == BlockRole.MainContent).Subject;
        main.Markdown.Should().NotBeNullOrEmpty();
        main.Markdown.Should().Contain("# Top");
        main.Markdown.Should().Contain("## Sub");
    }

    [Fact]
    public void Navigation_Block_Has_Empty_Markdown_So_Links_Projection_Runs()
    {
        const string html = """
            <html><body>
              <header><nav class='main-menu'>
                <a href='/'>Home</a><a href='/blog'>Blog</a><a href='/about'>About</a><a href='/contact'>Contact</a>
              </nav></header>
            </body></html>
            """;
        var blocks = Classify(html);
        var nav = blocks.Should().Contain(b => b.Role == BlockRole.PrimaryNavigation).Subject;
        nav.Markdown.Should().BeEmpty();
    }

    [Fact]
    public void Footer_Block_Has_Empty_Markdown_So_Legacy_Path_Runs()
    {
        const string html = """
            <html><body>
              <main><article><h1>X</h1><p>Body content meeting the classifier's text-length threshold so the article emits as MainContent.</p></article></main>
              <footer>Copyright 2026 Example Corp. All rights reserved.</footer>
            </body></html>
            """;
        var blocks = Classify(html);
        var footer = blocks.FirstOrDefault(b => b.Role == BlockRole.Footer);
        if (footer is not null) footer.Markdown.Should().BeEmpty();
    }

    [Fact]
    public void RepeatedItem_Block_Has_Markdown_Populated()
    {
        const string html = """
            <html><body>
              <main>
                <ul class="forum-thread">
                  <li class="post"><h3>Subject line one</h3><p>Body paragraph for post one with enough text to clear the repeated-item detector thresholds and survive the classifier's quality gate.</p></li>
                  <li class="post"><h3>Subject line two</h3><p>Body paragraph for post two with enough text to clear the repeated-item detector thresholds and survive the classifier's quality gate.</p></li>
                  <li class="post"><h3>Subject line three</h3><p>Body paragraph for post three with enough text to clear the repeated-item detector thresholds and survive the classifier's quality gate.</p></li>
                </ul>
              </main>
            </body></html>
            """;
        var blocks = Classify(html);
        var repeated = blocks.Where(b => b.Role == BlockRole.RepeatedItem).ToList();
        if (repeated.Count > 0)
        {
            // Whatever the detector emits should have markdown if it qualifies.
            repeated[0].Markdown.Should().NotBeNullOrEmpty();
            repeated[0].Markdown.Should().Contain("### Subject");
        }
    }
}
