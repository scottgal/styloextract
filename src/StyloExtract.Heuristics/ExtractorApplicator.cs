using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorApplicator : IExtractorApplicator
{
    private readonly ILogger<ExtractorApplicator>? _logger;

    public ExtractorApplicator(ILogger<ExtractorApplicator>? logger = null)
    {
        _logger = logger;
    }

    public ApplicatorResult Apply(IDocument document, LearnedExtractor extractor)
    {
        var result = new List<ExtractedBlock>();
        int i = 0;
        int rulesApplied = 0;
        int rulesMissed = 0;
        foreach (var rule in extractor.Rules)
        {
            // A rule "matched" when at least one of its selectors produced an element.
            // The aggregate miss count is the bug-out signal: when a CMS theme changes
            // and most selectors point at DOM paths that no longer exist, the missed
            // count approaches the rule count and LayoutExtractor can drop the cached
            // extractor for THIS request, re-classify with the heuristic, and refit.
            bool ruleMatched = false;
            foreach (var selector in rule.CssSelectors)
            {
                IElement[] matches;
                try
                {
                    matches = document.QuerySelectorAll(selector).ToArray();
                }
                catch (Exception ex)
                {
                    _logger?.LogWarning(ex, "ExtractorApplicator: bad CSS selector {Selector} on rule {RuleId}; skipping", selector, rule.RuleId);
                    continue;
                }
                if (matches.Length > 0) ruleMatched = true;
                foreach (var element in matches)
                {
                    result.Add(new ExtractedBlock
                    {
                        Id = $"b{i++:D4}",
                        Role = rule.Role,
                        Confidence = rule.MeanConfidence,
                        Text = element.TextContent.Trim(),
                        Markdown = ShouldRenderMarkdown(rule.Role) ? DomMarkdownWalker.Render(element) : "",
                        XPath = XPathBuilder.ComputeXPath(element),
                        CssSelector = selector,
                        TextLength = element.TextContent.Length,
                        LinkDensity = LinkDensityOf(element),
                        Links = element.QuerySelectorAll("a")
                            .Select(a => new ExtractedLink
                            {
                                Text = a.TextContent.Trim(),
                                Href = a.GetAttribute("href") ?? "",
                                IsExternal = (a.GetAttribute("href") ?? "").StartsWith("http", StringComparison.OrdinalIgnoreCase)
                            }).ToList()
                    });
                }
            }
            if (ruleMatched) rulesApplied++; else rulesMissed++;
        }
        return new ApplicatorResult(result, rulesApplied, rulesMissed);
    }

    private static bool ShouldRenderMarkdown(BlockRole role) => role
        is BlockRole.MainContent
        or BlockRole.Article
        or BlockRole.RepeatedItem
        or BlockRole.Summary
        or BlockRole.Title
        or BlockRole.Heading
        or BlockRole.Table
        or BlockRole.CodeBlock
        or BlockRole.Sidebar
        or BlockRole.RelatedLinks;

    private static double LinkDensityOf(IElement element)
    {
        var total = element.TextContent.Length;
        if (total == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / total;
    }
}
