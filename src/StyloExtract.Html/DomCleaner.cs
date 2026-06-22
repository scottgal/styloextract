using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Html;

public sealed class DomCleaner : IDomCleaner
{
    // Combined selector: AngleSharp walks the tree ONCE and matches any tag in the union.
    // The previous version ran five separate QuerySelectorAll passes (one per tag), each
    // a full tree scan. Bench 0496369 measured DomCleaner at 715 us / 522 KB on Medium
    // (~50 KB DOM); the combined selector roughly halves both numbers because the
    // dominant cost is tree traversal, not matching.
    private const string CombinedStripSelector = "script,style,template,noscript,svg";

    public void Clean(IDocument document)
    {
        var nodes = document.QuerySelectorAll(CombinedStripSelector).ToArray();
        foreach (var node in nodes)
        {
            node.Remove();
        }
    }
}
