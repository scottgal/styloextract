using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Regression tests for HeuristicBlockClassifier nav-detection (alpha.13).
///
/// Background: alpha.12 under-classified real-world nav patterns. On
/// www.mostlylucid.net the header was a &lt;ul&gt; of &lt;li&gt;-of-links
/// (no &lt;nav&gt; tag) — the classifier descended into deep
/// &lt;li&gt; &gt; &lt;a&gt; &gt; &lt;div&gt; wrappers and emitted them as
/// Boilerplate, producing a Sitemap CLI tree with just the page title. On
/// en.wikipedia.org/wiki/Markdown the top nav strips are real &lt;nav&gt;
/// tags with aria-label="Site" / "Personal tools" but landed as Sidebar
/// only (which Sitemap profile filters out).
///
/// Fixtures: tests/StyloExtract.Heuristics.Tests/Fixtures/ captured 2026-06-26
/// (not hand-trimmed; both files are ~200KB, under the 500KB ceiling).
/// </summary>
public class NavClassificationTests
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

    private static string ReadFixture(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        return File.ReadAllText(path);
    }

    [Fact]
    public void MostlyLucid_HeaderNav_IsClassifiedAsPrimaryNavigation()
    {
        var html = ReadFixture("mostlylucid-home.html");
        var (blocks, _) = Classify(html);

        // The mostlylucid header is a <ul> of <li>-of-links inside <header>
        // (no <nav> tag). Pre-alpha.13 this surfaced as deep-Boilerplate
        // rules (body > header > ul > li > a > div) and the Sitemap profile
        // dropped all of it. After the fix, at least one block must be
        // PrimaryNavigation and live inside the header.
        blocks.Should().Contain(b => b.Role == BlockRole.PrimaryNavigation,
            "the header <ul>-of-links must classify as PrimaryNavigation");

        var primary = blocks.Where(b => b.Role == BlockRole.PrimaryNavigation).ToList();
        primary.Should().NotBeEmpty();
        primary.Should().Contain(b => b.XPath != null && b.XPath.Contains("header"),
            "the PrimaryNavigation block must come from inside <header>");
    }

    [Fact]
    public void Wikipedia_TopNav_IsClassifiedAsPrimaryNavigation()
    {
        var html = ReadFixture("wikipedia-markdown.html");
        var (blocks, _) = Classify(html);

        // Wikipedia has multiple <nav aria-label=...> elements at the top
        // of the document — "Site", "Personal tools", "Appearance", "Contents".
        // At least one of them must surface as PrimaryNavigation (not just
        // Sidebar) so the Sitemap profile gets nav links to emit.
        blocks.Should().Contain(b => b.Role == BlockRole.PrimaryNavigation,
            "at least one Wikipedia top <nav> must be PrimaryNavigation, not Sidebar");
    }

    [Fact]
    public void NavWithAriaLabel_Breadcrumb_IsClassifiedAsBreadcrumb()
    {
        // Breadcrumb lives OUTSIDE <main>/<article> — that's the page-level breadcrumb
        // strip the Sitemap profile cares about. Breadcrumbs INSIDE the article are
        // intra-block contaminants and are handled by IntraBlockCleaner instead.
        var html = @"<html><body>
            <nav aria-label='Breadcrumb'>
                <ol>
                    <li><a href='/'>Home</a></li>
                    <li><a href='/docs'>Docs</a></li>
                    <li><a href='/docs/intro'>Intro</a></li>
                </ol>
            </nav>
            <main>
                <article><p>" + new string('x', 400) + @"</p></article>
            </main>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Breadcrumb,
            "a <nav aria-label='Breadcrumb'> must classify as Breadcrumb");
    }

    [Fact]
    public void OlWithBreadcrumbClass_IsClassifiedAsBreadcrumb()
    {
        var html = @"<html><body>
            <ol class='breadcrumb'>
                <li><a href='/'>Home</a></li>
                <li><a href='/docs'>Docs</a></li>
                <li>Current</li>
            </ol>
            <main>
                <article><p>" + new string('x', 400) + @"</p></article>
            </main>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.Breadcrumb,
            "an <ol class='breadcrumb'> must classify as Breadcrumb");
    }

    [Fact]
    public void FooterNav_IsClassifiedAsSecondaryNavigation()
    {
        var html = @"<html><body>
            <main><article><p>" + new string('x', 400) + @"</p></article></main>
            <footer>
                <nav>
                    <a href='/about'>About</a>
                    <a href='/contact'>Contact</a>
                    <a href='/privacy'>Privacy</a>
                    <a href='/terms'>Terms</a>
                </nav>
            </footer>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.SecondaryNavigation,
            "a <footer><nav> must classify as SecondaryNavigation");
    }

    [Fact]
    public void FooterUlOfLinks_IsClassifiedAsSecondaryNavigation()
    {
        var html = @"<html><body>
            <main><article><p>" + new string('x', 400) + @"</p></article></main>
            <footer>
                <ul>
                    <li><a href='/about'>About</a></li>
                    <li><a href='/contact'>Contact</a></li>
                    <li><a href='/privacy'>Privacy</a></li>
                    <li><a href='/terms'>Terms</a></li>
                </ul>
            </footer>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.SecondaryNavigation,
            "a <footer> with a <ul> of mostly-link <li>s must classify as SecondaryNavigation");
    }

    [Fact]
    public void RoleNavigation_IsClassifiedAsPrimaryNavigation()
    {
        var html = @"<html><body>
            <div role='navigation'>
                <a href='/'>Home</a>
                <a href='/about'>About</a>
                <a href='/blog'>Blog</a>
                <a href='/contact'>Contact</a>
            </div>
            <main><article><p>" + new string('x', 400) + @"</p></article></main>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.PrimaryNavigation,
            "a <* role='navigation'> must classify as PrimaryNavigation");
    }

    [Fact]
    public void HeaderUlOfLinks_IsClassifiedAsPrimaryNavigation_NotDeepBoilerplate()
    {
        // The mostlylucid pattern, minimized: header > ul > li > a > div
        // Pre-alpha.13: the deep <div> inside <li> got classified as
        // Boilerplate via the link-density fall-through. Post-fix: the
        // classification bubbles up to the <ul> as PrimaryNavigation and
        // descent is suppressed.
        var html = @"<html><body>
            <header id='header'>
                <ul>
                    <li><a href='/'><div>Home</div></a></li>
                    <li><a href='/blog'><div>Blog</div></a></li>
                    <li><a href='/about'><div>About</div></a></li>
                    <li><a href='/contact'><div>Contact</div></a></li>
                </ul>
            </header>
            <main><article><p>" + new string('x', 400) + @"</p></article></main>
        </body></html>";

        var (blocks, _) = Classify(html);
        blocks.Should().Contain(b => b.Role == BlockRole.PrimaryNavigation,
            "the header <ul>-of-links must classify as PrimaryNavigation");

        // The deep <div>s inside <a> must NOT all surface as their own
        // Boilerplate blocks — they must be suppressed by the parent-bubble.
        var deepBoilerplate = blocks
            .Where(b => b.Role == BlockRole.Boilerplate
                        && b.XPath != null
                        && b.XPath.Contains("header"))
            .ToList();
        deepBoilerplate.Should().BeEmpty(
            "after classifying the header <ul> as PrimaryNavigation, the heuristic must not also emit deep header descendants as Boilerplate");
    }
}
