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
    //
    // The strip set covers two categories: hidden/structural (script/style/template/
    // noscript/svg) whose serialized text is never visible content; and <select>,
    // whose <option> children are pure form scaffolding ("All", ".NET (41)", "Newest"
    // / "Oldest" / "A-Z" / "Z-A" etc on a typical blog category dropdown) and were
    // the largest leak source on pages with category/sort dropdowns. <input> and
    // <textarea> are deliberately NOT stripped because HeuristicBlockClassifier
    // needs to see them to identify forms as Form-role blocks for the
    // AgentNavigation profile and IntraBlockCleaner's meaningful-input check.
    // <nav>, <form>, <button>, <label> are also kept for the same reason — the
    // classifier and IntraBlockCleaner handle them with role-aware policies.
    //
    // Note: <script type="application/ld+json"> blobs are stripped too. The JSON-LD
    // fallback in LayoutExtractor must read those scripts BEFORE this method runs.
    private const string CombinedStripSelector =
        "script,style,template,noscript,svg,select";

    public void Clean(IDocument document)
    {
        var nodes = document.QuerySelectorAll(CombinedStripSelector).ToArray();
        foreach (var node in nodes)
        {
            node.Remove();
        }
    }
}
