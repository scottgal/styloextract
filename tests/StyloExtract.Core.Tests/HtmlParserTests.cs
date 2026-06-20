using AngleSharp.Dom;
using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Core.Tests;

public class HtmlParserTests
{
    [Fact]
    public void Parse_ProducesDocumentWithExpectedTitleAndBodyTag()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        const string html = "<!DOCTYPE html><html><head><title>Hello</title></head><body><h1>Hi</h1></body></html>";

        IDocument doc = parser.Parse(html);

        doc.Title.Should().Be("Hello");
        doc.Body!.QuerySelector("h1")!.TextContent.Should().Be("Hi");
    }

    [Fact]
    public void Parse_ToleratesMalformedHtml()
    {
        IHtmlDomParser parser = new AngleSharpHtmlDomParser();
        const string html = "<html><body><div>oops<p>unclosed";

        IDocument doc = parser.Parse(html);

        doc.Body!.QuerySelector("p")!.TextContent.Should().Contain("unclosed");
    }
}
