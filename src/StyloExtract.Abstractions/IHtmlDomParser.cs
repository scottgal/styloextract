using AngleSharp.Dom;

namespace StyloExtract.Abstractions;

public interface IHtmlDomParser
{
    IDocument Parse(string html, Uri? sourceUri = null);
}
