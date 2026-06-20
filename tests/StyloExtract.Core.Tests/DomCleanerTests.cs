using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class DomCleanerTests
{
    [Fact]
    public void Clean_RemovesScriptStyleTemplateNoscriptSvg()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        IDomCleaner cleaner = new DomCleaner();
        const string html = """
            <html><body>
              <script>alert(1)</script>
              <style>.x{color:red}</style>
              <template id="t"><div>hi</div></template>
              <noscript>no js</noscript>
              <svg><circle/></svg>
              <p>keep me</p>
            </body></html>
            """;

        IDocument doc = parser.Parse(html);
        cleaner.Clean(doc);

        doc.QuerySelectorAll("script").Should().BeEmpty();
        doc.QuerySelectorAll("style").Should().BeEmpty();
        doc.QuerySelectorAll("template").Should().BeEmpty();
        doc.QuerySelectorAll("noscript").Should().BeEmpty();
        doc.QuerySelectorAll("svg").Should().BeEmpty();
        doc.QuerySelector("p")!.TextContent.Should().Be("keep me");
    }
}
