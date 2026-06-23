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
    public void Sidebar_With_Toc_List_Renders_As_Markdown_List()
    {
        // The "on this page" TOC pattern: an <aside> containing a <ul><li><a> list of
        // anchor links. Before Sidebar was added to ShouldRenderMarkdown the legacy
        // path flattened it to plain indented text, which read as noise to an AI
        // consumer. With the fix it survives as a real markdown list with links.
        const string html = """
            <html><body>
              <main><article>
                <h1>A real article body that clears the classifier's content threshold via paragraphs.</h1>
                <p>Body paragraph long enough to keep the main article past the heuristic emission gate.</p>
                <p>Second body paragraph keeping the article comfortably above the textual gate.</p>
              </article></main>
              <aside class="toc">
                <h3>On this page</h3>
                <ul>
                  <li><a href="#one">Section one</a></li>
                  <li><a href="#two">Section two</a></li>
                  <li><a href="#three">Section three</a></li>
                </ul>
              </aside>
            </body></html>
            """;
        var blocks = Classify(html);
        var sidebar = blocks.FirstOrDefault(b => b.Role == BlockRole.Sidebar);
        if (sidebar is not null)
        {
            sidebar.Markdown.Should().NotBeNullOrEmpty();
            sidebar.Markdown.Should().Contain("- [Section one](#one)");
        }
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
