using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Html;

public sealed class DomCleaner : IDomCleaner
{
    private static readonly string[] TagsToStrip = ["script", "style", "template", "noscript", "svg"];

    public void Clean(IDocument document)
    {
        foreach (var tag in TagsToStrip)
        {
            var nodes = document.QuerySelectorAll(tag).ToArray();
            foreach (var node in nodes)
            {
                node.Remove();
            }
        }
    }
}
