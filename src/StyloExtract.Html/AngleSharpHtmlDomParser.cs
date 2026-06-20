using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using StyloExtract.Abstractions;

namespace StyloExtract.Html;

public sealed class AngleSharpHtmlDomParser : IHtmlDomParser
{
    private readonly HtmlParser _parser;

    public AngleSharpHtmlDomParser()
    {
        var context = BrowsingContext.New(Configuration.Default);
        _parser = new HtmlParser(new HtmlParserOptions(), context);
    }

    public IDocument Parse(string html, Uri? sourceUri = null)
    {
        return _parser.ParseDocument(html);
    }
}
