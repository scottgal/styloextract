using AngleSharp.Dom;

namespace StyloExtract.Heuristics;

/// <summary>
/// Builds an XPath expression for a DOM element by walking its ancestor chain.
/// Shared between <see cref="HeuristicBlockClassifier"/> and <see cref="ExtractorApplicator"/>.
/// </summary>
public static class XPathBuilder
{
    /// <summary>
    /// Returns an absolute XPath string for the given element, e.g. /html[1]/body[1]/main[1].
    /// Returns an empty string if the element has no parent (root or detached node).
    /// </summary>
    public static string ComputeXPath(IElement element)
    {
        var parts = new Stack<string>();
        var current = (IElement?)element;
        while (current is not null && current.ParentElement is not null)
        {
            var idx = 1;
            var sibling = current.PreviousElementSibling;
            while (sibling is not null)
            {
                if (sibling.TagName == current.TagName) idx++;
                sibling = sibling.PreviousElementSibling;
            }
            parts.Push($"{current.LocalName}[{idx}]");
            current = current.ParentElement;
        }
        return parts.Count > 0 ? "/" + string.Join("/", parts) : "";
    }
}
