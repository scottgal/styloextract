using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorApplicator : IExtractorApplicator
{
    public IReadOnlyList<ExtractedBlock> Apply(IDocument document, LearnedExtractor extractor)
    {
        var result = new List<ExtractedBlock>();
        int i = 0;
        foreach (var rule in extractor.Rules)
        {
            foreach (var selector in rule.CssSelectors)
            {
                IElement[] matches;
                try
                {
                    matches = document.QuerySelectorAll(selector).ToArray();
                }
                catch
                {
                    continue; // bad selector — skip
                }
                foreach (var element in matches)
                {
                    result.Add(new ExtractedBlock
                    {
                        Id = $"b{i++:D4}",
                        Role = rule.Role,
                        Confidence = rule.MeanConfidence,
                        Text = element.TextContent.Trim(),
                        Markdown = "",
                        XPath = "",
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
        }
        return result;
    }

    private static double LinkDensityOf(IElement element)
    {
        var total = element.TextContent.Length;
        if (total == 0) return 0;
        var linkText = element.QuerySelectorAll("a").Sum(a => a.TextContent.Length);
        return (double)linkText / total;
    }
}
