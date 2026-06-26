using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

/// <summary>
/// Detects the page-level <c>&lt;h1&gt;</c> — the single H1 the rest of the page is
/// "about" — and projects it as a <see cref="BlockRole.Title"/> block.
///
/// <para>
/// Selection rule: prefer an H1 that lives inside <c>&lt;main&gt;</c> or
/// <c>&lt;article&gt;</c>; with multiple H1s elsewhere, the earliest-in-document
/// order wins. Confidence is 0.95 when exactly one H1 is present, lower otherwise.
/// </para>
///
/// <para>
/// The detector runs independently of <see cref="HeuristicBlockClassifier"/>'s
/// segment / classify / greedy-select pipeline because the page's H1 typically
/// lives nested inside an accepted MainContent ancestor, which the overlap-rejection
/// logic would otherwise drop. The Title block is a separate logical projection of
/// the H1's text — not a competing extraction — so it's safe to emit alongside.
/// </para>
/// </summary>
public static class PageTitleDetector
{
    /// <summary>
    /// Walk the document for the page's primary <c>&lt;h1&gt;</c> and return it
    /// as a <see cref="ExtractedBlock"/> with role <see cref="BlockRole.Title"/>.
    /// Returns <c>null</c> when the page has no usable H1.
    /// </summary>
    public static ExtractedBlock? Detect(IReadOnlyList<IElement> segments)
    {
        if (segments.Count == 0) return null;
        var body = segments[0].Owner?.Body;
        return body is null ? null : Detect(body);
    }

    /// <summary>
    /// Walk <paramref name="document"/>'s body for the page's primary <c>&lt;h1&gt;</c>
    /// and return it as a Title block. Returns <c>null</c> when the page has no usable H1.
    /// </summary>
    public static ExtractedBlock? Detect(IDocument document)
    {
        return document.Body is null ? null : Detect(document.Body);
    }

    private static ExtractedBlock? Detect(IElement body)
    {
        var allH1 = body.QuerySelectorAll("h1")
            .Where(h => !string.IsNullOrWhiteSpace(h.TextContent))
            .ToList();
        if (allH1.Count == 0) return null;

        IElement chosen;
        double confidence;
        // Prefer an H1 inside <main>/<article>; fall back to earliest in document.
        var inSemantic = allH1.FirstOrDefault(h => HasAncestor(h, "main") || HasAncestor(h, "article"));
        if (inSemantic is not null)
        {
            chosen = inSemantic;
            confidence = allH1.Count == 1 ? 0.95 : 0.85;
        }
        else
        {
            chosen = allH1[0];
            confidence = allH1.Count == 1 ? 0.95 : 0.7;
        }

        var text = chosen.TextContent.Trim();
        // Render via DomMarkdownWalker so the on-the-wire Markdown matches what the
        // ExtractorApplicator path produces when the induced template's Title rule
        // matches the same H1. Keeping both code paths' Markdown identical is what
        // lets the response-cache ETag stay stable across the heuristic-classify
        // first request and the applicator-bound subsequent requests.
        return new ExtractedBlock
        {
            Id = "title",
            Role = BlockRole.Title,
            Confidence = confidence,
            Text = text,
            Markdown = DomMarkdownWalker.Render(chosen),
            XPath = XPathBuilder.ComputeXPath(chosen),
            CssSelector = null,
            TextLength = text.Length,
            LinkDensity = 0.0,
            Links = chosen.QuerySelectorAll("a")
                .Select(a => new ExtractedLink
                {
                    Text = a.TextContent.Trim(),
                    Href = a.GetAttribute("href") ?? "",
                    IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
                })
                .ToList()
        };
    }

    private static bool HasAncestor(IElement element, string tag)
    {
        var current = element.ParentElement;
        while (current is not null)
        {
            if (current.TagName.Equals(tag, StringComparison.OrdinalIgnoreCase)) return true;
            current = current.ParentElement;
        }
        return false;
    }
}
