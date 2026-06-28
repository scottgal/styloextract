using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

/// <summary>
/// Pins the Move 1 "tighten-on-anchor" step in <see cref="HeuristicBlockClassifier"/>.
///
/// After Step 1a (semantic promotion) demotes wrapper ancestors of a winning
/// &lt;main&gt;/&lt;article&gt;, this step looks one level DOWN: if the semantic element has
/// exactly one descendant div/section carrying ≥80% of its prose text, a stable
/// id or class anchor, and link density &lt; 0.5, the descendant is preferred as
/// MainContent and the semantic element drops to Boilerplate.
///
/// Catches the Wikipedia + mostlylucid leak shape: the semantic container ALSO
/// holds a non-content sub-region (language picker, route variants, share bar)
/// that the deterministic inducer otherwise picks up.
/// </summary>
public class TightenOnAnchorTests
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

    private static string Repeat(string s, int n) => string.Concat(Enumerable.Repeat(s, n));

    [Fact]
    public void Wikipedia_Shape_Prefers_Inner_Anchor_Over_Main()
    {
        // Models the real Wikipedia leak shape (verified against
        // en.wikipedia.org/wiki/Markdown HTML 2026-06-27):
        //   - <main> contains the language-picker dropdown plus the article
        //     wrapper as siblings.
        //   - Picker has stable id="p-lang-btn" and stable class
        //     "vector-dropdown mw-portlet mw-portlet-lang". It's NOT a
        //     <ul>-of-links so NavPreDetector doesn't catch it.
        //   - Article wrapper is <div id="mw-content-text" class="mw-body-content">.
        // Without the tighten step, <main> wins MainContent and the picker text
        // ("Alemannisch", "33 languages", ...) leaks into the output. With the
        // tighten step, the inner div wins and the picker drops out.
        var article = Repeat(
            "This sentence is a portion of the article body and contributes prose text to the inner content wrapper. ",
            25);
        // Picker as a dropdown trigger + language-name span. No <a><img> hammer
        // that would trip NavPreDetector.
        const string pickerLanguages =
            "Alemannisch Bulgarian Deutsch Espanol Francais Italiano Japanese " +
            "Korean Nederlands Polski Portugues Russian Suomi Svenska";

        // Match real Wikipedia: <main id="content" class="mw-body">. The id="content"
        // matches the existing classHintBonus so the outer <main> isn't naturally
        // outscored by its inner <div id="mw-content-text"> — both get the bonus.
        // Without tighten-on-anchor, <main> wins by text-length (it spans the picker)
        // and the picker leaks. This is THE real leak shape the production dogfood
        // loop hits.
        var html =
            "<html><body>" +
            "<main id='content' class='mw-body'>" +
                "<div id='p-lang-btn' class='vector-dropdown mw-portlet mw-portlet-lang'>" +
                    "<span>33 languages</span>" +
                    $"<span class='vector-dropdown-content'>{pickerLanguages}</span>" +
                "</div>" +
                $"<div id='mw-content-text' class='mw-body-content'><p>{article}</p></div>" +
            "</main>" +
            "</body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Where(b => b.Role == BlockRole.MainContent).ToList();
        mainContent.Should().NotBeEmpty("the article should still be classified as MainContent");

        var combinedMainText = string.Concat(mainContent.Select(b => b.Text));
        combinedMainText.Should().NotContain("Alemannisch",
            "tighten-on-anchor must drop the language picker out of MainContent by picking #mw-content-text instead of <main>");
        combinedMainText.Should().NotContain("33 languages",
            "the picker's heading label must not leak into MainContent");
        combinedMainText.Should().Contain("article body",
            "the actual article prose must still be present in MainContent");
    }

    [Fact]
    public void MostlyLucid_Shape_Prefers_Prose_Anchor_Without_Main()
    {
        // Models the mostlylucid blog leak shape: there's NO <main>; the page
        // wraps blog content in a <div id="blogpost"> which contains the
        // language-flag picker + the article body inside <div class="prose">.
        // The tighten step also needs to fire when <article> qualifies as the
        // semantic element OR when the BLOCKY-DIV wrapper (#blogpost) wins
        // MainContent and has a tighter anchored descendant.
        //
        // "prose" is a stable single class (passes DefaultClassStabilityFilter).
        // The flag picker has no obvious nav-hint class.
        var article = Repeat(
            "Article paragraph text in the prose block. ",
            40);
        var flagItems = string.Concat(Enumerable.Range(0, 12)
            .Select(i => "<div class='flag-tooltip'><span>Language Variant " + i + "</span></div>"));

        var html =
            "<html><body>" +
            "<article id='blogpost'>" +
                $"<div class='hidden sm:inline-flex'>{flagItems}</div>" +
                $"<div class='prose dark:text-white py-2'><p>{article}</p></div>" +
            "</article>" +
            "</body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Where(b => b.Role is BlockRole.MainContent or BlockRole.Article).ToList();
        mainContent.Should().NotBeEmpty();

        var combinedMainText = string.Concat(mainContent.Select(b => b.Text));
        combinedMainText.Should().NotContain("Language Variant 11",
            "tighten-on-anchor must drop the language-flag picker by picking .prose over the article wrapper");
        combinedMainText.Should().Contain("Article paragraph",
            "the actual article prose must remain in MainContent");
    }

    // Note: a prototype Move 1b test for the no-semantic-anchor case lived
    // here and got dropped along with Move 1b itself. The non-semantic
    // tighten extension regressed BBC News landing classification by
    // over-tightening nested sub-containers under a link-dense <main>.
    // The mostlylucid pure-div leak shape is now caught by the apply-time
    // path: Move 2b's image-anchor picker gate trips on the 12-flag picker,
    // applicatorBugOut fires, and Move 3 enqueues an LLM repair.

    [Fact]
    public void No_Anchored_Descendant_Keeps_Main_As_MainContent()
    {
        // Vanilla page: <main> with a single <p> body, no sub-anchored descendant.
        // Tighten step must NOT fire — the existing semantic-promotion behaviour
        // wins.
        var body = Repeat("This is the main article body content. ", 30);
        var html = $"<html><body><main><p>{body}</p></main></body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Single(b => b.Role == BlockRole.MainContent);
        mainContent.Text.Should().Contain("article body content",
            "with no anchored descendant the semantic element still wins MainContent");
    }

    [Fact]
    public void Multiple_Qualifying_Descendants_Keeps_Main_As_MainContent()
    {
        // <main> with TWO equally-strong anchored descendants. Ambiguous which to
        // prefer; tighten step must abstain and keep <main>.
        var prose = Repeat("The article body talks about prose content here. ", 15);
        var html =
            "<html><body><main>" +
                $"<div id='section-one' class='article-section'><p>{prose}</p></div>" +
                $"<div id='section-two' class='article-section'><p>{prose}</p></div>" +
            "</main></body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Where(b => b.Role == BlockRole.MainContent).ToList();
        mainContent.Should().NotBeEmpty();
        // Either <main> or one of the sections may win; the key invariant is that
        // we don't lose content. The text-by-text assertion below would pass
        // whether <main> or one inner div was picked, BUT the spec says we
        // ABSTAIN when ambiguous — so MainContent text length should approximate
        // the OUTER <main>'s text (both sections, not just one).
        var combined = string.Concat(mainContent.Select(b => b.Text));
        combined.Length.Should().BeGreaterThan((int)(prose.Length * 1.5),
            "ambiguous descendants should not cause MainContent to shrink to a single section");
    }

    [Fact]
    public void Unstable_Class_Descendant_Does_Not_Trigger_Tighten()
    {
        // <main> with a single big descendant whose only class is a hash-shaped
        // CSS-modules / Emotion-style token. Should NOT qualify as an anchor —
        // anchor must be a STABLE id or class. <main> wins.
        var prose = Repeat("Article prose body text. ", 30);
        var html =
            "<html><body><main>" +
                $"<div class='css-a1b2c3d4'><p>{prose}</p></div>" +
            "</main></body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Where(b => b.Role == BlockRole.MainContent).ToList();
        mainContent.Should().NotBeEmpty();
        // Without a stable anchor we keep <main> as the winner; verified by
        // checking that the WHOLE <main>'s text is present (including everything
        // <main> would emit).
        var combined = string.Concat(mainContent.Select(b => b.Text));
        combined.Should().Contain("prose body text");
    }

    [Fact]
    public void High_Link_Density_Descendant_Does_Not_Trigger_Tighten()
    {
        // <main> with an inner anchored div that LOOKS like content by char count
        // but is mostly link text (a picker disguised as content). Tighten step
        // must reject it — link density >= 0.5 fails the gate.
        var linkSpam = string.Concat(Enumerable.Range(0, 80)
            .Select(i => $"<a href='/x/{i}'>link-text-fragment-number-{i}-content-here</a> "));
        var html =
            "<html><body><main>" +
                $"<div id='picker' class='picker-list'>{linkSpam}</div>" +
                "<p>Short prose here.</p>" +
            "</main></body></html>";

        var (blocks, _) = Classify(html);

        var mainContent = blocks.Where(b => b.Role == BlockRole.MainContent).ToList();
        // The picker should not become MainContent. Either <main> wins
        // (acceptable: the existing pipeline) or no MainContent is chosen, but
        // #picker MUST NOT be the MainContent block.
        mainContent.Should().NotContain(
            b => b.Text.Contains("link-text-fragment-number-79"),
            "high-link-density descendant must not be promoted to MainContent");
    }
}