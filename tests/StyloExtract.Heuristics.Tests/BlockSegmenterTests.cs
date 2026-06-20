using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class BlockSegmenterTests
{
    [Fact]
    public void Segment_ReturnsSemanticTagsAndBlockyDivs()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IBlockSegmenter segmenter = new BlockSegmenter();
        const string html = """
            <html><body>
              <header><nav><a href='/'>Home</a><a href='/about'>About</a></nav></header>
              <main>
                <article><h1>Title</h1><p>This is the article body with plenty of text inside it.</p></article>
                <aside><a>r1</a><a>r2</a><a>r3</a></aside>
              </main>
              <footer>Copyright 2026</footer>
              <div>tiny</div>
            </body></html>
            """;
        IDocument doc = parser.Parse(html);

        IReadOnlyList<IElement> blocks = segmenter.Segment(doc);

        var tags = blocks.Select(b => b.TagName.ToLowerInvariant()).ToHashSet();
        tags.Should().Contain(new[] { "header", "nav", "main", "article", "aside", "footer" });
        tags.Should().NotContain("div");
    }
}
