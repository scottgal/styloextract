using FluentAssertions;
using StyloExtract.Core.Skeleton;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class DomSkeletonRendererTests
{
    private static string Render(string body, SkeletonRenderOptions? options = null)
    {
        // Mirror production: DomCleaner physically removes script/style/
        // noscript/comments BEFORE the renderer runs, so descendant
        // TextContent in the renderer's excerpts doesn't include their
        // payloads. The renderer assumes a cleaned DOM.
        var parser = new AngleSharpHtmlDomParser();
        var cleaner = new DomCleaner();
        var doc = parser.Parse($"<html><body>{body}</body></html>");
        cleaner.Clean(doc);
        return new DomSkeletonRenderer(options).Render(doc);
    }

    [Fact]
    public void Empty_Document_Returns_Empty()
    {
        var parser = new AngleSharpHtmlDomParser();
        var doc = parser.Parse("<html></html>");
        new DomSkeletonRenderer().Render(doc).Should().BeEmpty();
    }

    [Fact]
    public void Body_Root_Appears_First()
    {
        Render("<main><p>x</p></main>")
            .Should().StartWith("ROOT body\n");
    }

    [Fact]
    public void Article_With_Heading_And_Two_Paragraphs_Emits_Tree()
    {
        var md = Render("""
            <main>
              <article>
                <h1>Title here</h1>
                <p>First paragraph body content with some meaningful length.</p>
                <p>Second paragraph keeping the article above the gate.</p>
              </article>
            </main>
            """);
        md.Should().Contain("main");
        md.Should().Contain("article");
        md.Should().Contain("h1");
        md.Should().Contain("Title here");
    }

    [Fact]
    public void Drops_Script_Style_Noscript_Svg_Template_Elements_From_Tree()
    {
        var md = Render("""
            <main>
              <h1>Visible</h1>
              <script>doStuff()</script>
              <style>p { color: red; }</style>
              <noscript>no js</noscript>
              <svg><circle/></svg>
              <template>tmpl content</template>
            </main>
            """);
        // What we ACTUALLY want is: none of these tags appear as their own
        // tree rows. The DomCleaner the test helper runs (mirroring
        // production) physically removes script/style/noscript before the
        // renderer sees the document. <svg> and <template> the renderer
        // drops itself in IsDropped().
        md.Should().Contain("Visible");
        foreach (var droppedTag in new[] { "script", "style", "noscript", "svg", "template" })
        {
            // No row begins with one of these tags (after the tree-drawing chars).
            foreach (var line in md.Split('\n'))
            {
                line.Should().NotMatchRegex($"^.*[├└]─ {droppedTag}([. ]|$)",
                    because: $"<{droppedTag}> must not appear as a tree row");
            }
        }
    }

    [Fact]
    public void Class_Tokens_Survive_When_Real_And_Are_Dropped_When_Hash_Shaped()
    {
        // .product-detail (real semantic) survives; the hash-shaped junk is dropped.
        var md = Render("""<div class="product-detail aB3xLm9kQ7rZw a83hG2vNqLpzMxR">x</div>""");
        md.Should().Contain(".product-detail");
        md.Should().NotContain("aB3xLm9kQ7rZw");
        md.Should().NotContain("a83hG2vNqLpzMxR");
    }

    [Fact]
    public void Repeated_Sibling_Run_Collapses_To_Summary_Plus_Exemplars()
    {
        var liItems = string.Concat(Enumerable.Range(0, 10)
            .Select(i => $"<li>Item number {i}</li>"));
        var md = Render($"<ul>{liItems}</ul>");
        md.Should().Contain("10 repeated li children");
        md.Should().Contain("3 exemplars below");
        // The first three Item numbers appear; the remainder are collapsed.
        md.Should().Contain("Item number 0");
        md.Should().Contain("Item number 1");
        md.Should().Contain("Item number 2");
        md.Should().NotContain("Item number 9");
    }

    [Fact]
    public void Repeated_Group_Threshold_Is_Configurable()
    {
        var liItems = string.Concat(Enumerable.Range(0, 4)
            .Select(i => $"<li>Item {i}</li>"));
        // 4 items below default threshold of 5 → no collapse.
        var defaultMd = Render($"<ul>{liItems}</ul>");
        defaultMd.Should().NotContain("repeated li children");

        // Lower the threshold to 3 → 4 items collapse.
        var lowMd = Render($"<ul>{liItems}</ul>",
            new SkeletonRenderOptions { RepeatedRunMinSize = 3 });
        lowMd.Should().Contain("4 repeated li children");
    }

    [Fact]
    public void Element_Summary_Includes_Text_Excerpt()
    {
        var md = Render("<p>Hello world, this is a paragraph with prose.</p>");
        md.Should().Contain("Hello world, this is a paragraph");
    }

    [Fact]
    public void Text_Excerpt_Caps_At_Configured_Length()
    {
        var longText = new string('a', 500);
        var md = Render($"<p>{longText}</p>",
            new SkeletonRenderOptions { MaxExcerptChars = 30 });
        // The summary line should not contain 500 'a's verbatim; should truncate.
        var lines = md.Split('\n').Where(l => l.Contains('a')).ToList();
        lines.Any(l => l.Contains(new string('a', 31))).Should().BeFalse();
        md.Should().Contain("…");
    }

    [Fact]
    public void Excerpt_Normalises_Whitespace_And_Escapes_Double_Quotes()
    {
        var md = Render("<p>line one\n    line two   \"quoted\"</p>");
        // Internal whitespace collapses to single space; " becomes '.
        md.Should().Contain("line one line two 'quoted'");
        md.Should().NotContain("\"quoted\"");
    }

    [Fact]
    public void Link_Density_Surfaces_When_Above_Threshold()
    {
        // <nav>HomeAboutContact</nav> — every char is inside an anchor.
        var md = Render("<nav><a href=\"/\">Home</a><a href=\"/about\">About</a><a href=\"/contact\">Contact</a></nav>");
        md.Should().Contain("linkDensity=1.00");
    }

    [Fact]
    public void Link_Density_Hidden_When_Below_Threshold()
    {
        var md = Render("<p>Just plain text, no links here at all.</p>");
        md.Should().NotContain("linkDensity");
    }

    [Fact]
    public void Depth_Cap_Truncates_Very_Deep_Trees()
    {
        // 10 nested divs; depth cap of 4 means the deepest one shouldn't
        // render AS ITS OWN TREE ROW. Text content of the deep leaf still
        // appears in the depth-1 ancestor's excerpt (because TextContent
        // is naturally recursive); we only assert the leaf isn't a row.
        var deep = string.Concat(Enumerable.Range(0, 10).Select(_ => "<div>")) +
                   "<span class='leaf'>deep leaf marker</span>" +
                   string.Concat(Enumerable.Range(0, 10).Select(_ => "</div>"));
        var md = Render(deep, new SkeletonRenderOptions { MaxDepth = 4 });
        md.Should().NotContain(".leaf");
        // The deeply-nested span isn't its own row, but its TEXT appears
        // in some ancestor's excerpt (truncated to MaxExcerptChars). What
        // we assert is the tree's deepest indent doesn't go past depth 4.
        foreach (var line in md.Split('\n'))
        {
            int barCount = line.Count(c => c == '│');
            barCount.Should().BeLessThan(5);
        }
    }

    [Fact]
    public void Output_Stays_Reasonable_Size_On_Medium_Article()
    {
        // Sanity: a Wikipedia-shape article should produce <12KB of skeleton.
        var body = "<main><article>" +
                   "<h1>Title</h1>" +
                   string.Concat(Enumerable.Range(0, 30)
                       .Select(i => $"<h2>Section {i}</h2><p>{new string('p', 200)}</p>")) +
                   "</article></main>";
        var md = Render(body);
        md.Length.Should().BeLessThan(12 * 1024);
        md.Length.Should().BeGreaterThan(100);
    }

    [Fact]
    public void Id_Attribute_Surfaces_When_Real()
    {
        var md = Render("""<main id="main-content"><p>x</p></main>""");
        md.Should().Contain("#main-content");
    }

    [Fact]
    public void Children_And_TextLen_Surface_When_Present()
    {
        var md = Render("<section><p>a</p><p>b</p><p>c</p></section>");
        md.Should().Contain("children=3");
        md.Should().Contain("textLen=3");
    }
}
