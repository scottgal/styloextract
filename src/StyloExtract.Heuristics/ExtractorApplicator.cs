using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorApplicator : IExtractorApplicator
{
    private readonly IClassStabilityFilter _classFilter;
    private readonly ILogger<ExtractorApplicator>? _logger;

    public ExtractorApplicator() : this(new DefaultClassStabilityFilter(), logger: null) { }

    public ExtractorApplicator(IClassStabilityFilter classFilter)
        : this(classFilter, logger: null) { }

    public ExtractorApplicator(ILogger<ExtractorApplicator>? logger)
        : this(new DefaultClassStabilityFilter(), logger) { }

    public ExtractorApplicator(IClassStabilityFilter classFilter, ILogger<ExtractorApplicator>? logger)
    {
        _classFilter = classFilter;
        _logger = logger;
    }

    public ApplicatorResult Apply(IDocument document, LearnedExtractor extractor)
    {
        var result = new List<ExtractedBlock>();
        int i = 0;
        int rulesApplied = 0;
        int rulesMissed = 0;
        // Title detection runs independently of the extractor's role-based selectors:
        // the page's primary <h1> is a stable, structural fact regardless of the
        // learned template. Surface it first so the Sitemap profile sees it on the
        // applicator path too — matches the heuristic-classifier output shape.
        var titleBlock = PageTitleDetector.Detect(document);
        if (titleBlock is not null)
        {
            // Skip Title injection when a rule already targets the Title role to avoid
            // duplicate Title blocks if an operator hand-authored one explicitly.
            if (!extractor.Rules.Any(r => r.Role == BlockRole.Title))
            {
                result.Add(titleBlock with { Id = $"b{i++:D4}" });
            }
        }
        foreach (var rule in extractor.Rules)
        {
            // Dispatch: identity-claim chain (Task 2+ inducer output) takes the
            // claim-based evaluator; legacy templates lacking Claims keep the
            // CSS-string path so persisted blobs from before Task 2 still apply.
            bool ruleMatched;
            if (rule.Claims is { Count: > 0 } claims)
            {
                ruleMatched = ApplyClaimRule(document, rule, claims, result, ref i);
            }
            else
            {
                ruleMatched = ApplyCssRule(document, rule, result, ref i);
            }
            if (ruleMatched) rulesApplied++; else rulesMissed++;
        }
        return new ApplicatorResult(result, rulesApplied, rulesMissed);
    }

    private bool ApplyClaimRule(
        IDocument document,
        BlockRule rule,
        IReadOnlyList<IdentityClaim> claims,
        List<ExtractedBlock> result,
        ref int i)
    {
        var matches = IdentityClaimApplicator.Apply(claims, document, _classFilter);
        if (matches.Count == 0) return false;

        // Render the chain to a CSS-selector string for the ExtractedBlock.CssSelector
        // field so downstream consumers (diagnostics, UI inspectors) keep a
        // human-readable selector reference. The string is the same shape
        // the inducer wrote into BlockRule.CssSelectors[0].
        var selector = rule.CssSelectors.Count > 0
            ? rule.CssSelectors[0]
            : IdentityClaimSelectorBuilder.ToCssSelector(claims);

        foreach (var element in matches)
        {
            result.Add(BuildBlock(element, rule, selector, i++));
        }
        return true;
    }

    private bool ApplyCssRule(
        IDocument document,
        BlockRule rule,
        List<ExtractedBlock> result,
        ref int i)
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
                result.Add(BuildBlock(element, rule, selector, i++));
            }
        }
        return ruleMatched;
    }

    private static ExtractedBlock BuildBlock(IElement element, BlockRule rule, string selector, int id)
    {
        return new ExtractedBlock
        {
            Id = $"b{id:D4}",
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
        };
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
