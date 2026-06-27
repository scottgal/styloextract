using AngleSharp.Dom;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorInducer : IExtractorInducer
{
    private readonly IClassStabilityFilter _classFilter;

    // Per-rule confidence penalty when the identity-claim builder hits its
    // ancestor-depth cap without becoming unique. Signals "we anchored, but
    // not as cleanly as we'd like - downstream drift detection should weight
    // this rule less heavily".
    private const double DepthCapConfidencePenalty = 0.1;

    public ExtractorInducer() : this(new DefaultClassStabilityFilter()) { }

    public ExtractorInducer(IClassStabilityFilter classFilter)
    {
        _classFilter = classFilter;
    }

    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks) =>
        Induce(templateId, blocks, document: null);

    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks, IDocument? document)
    {
        // Per-block emit: when a document is available, route through the
        // identity-claim builder; otherwise fall back to the legacy XPath →
        // CSS-string generalizer (kept for callers like RefitOrchestrator that
        // don't currently plumb the document).
        var perBlock = new List<(BlockRole Role, string CssSelector, IdentityClaim[]? Claims, double Confidence)>(blocks.Count);
        foreach (var b in blocks)
        {
            EmitForBlock(b, document, out var css, out var claims, out var confidence);
            perBlock.Add((b.Role, css, claims, confidence));
        }

        // Group by (role, css) so duplicate selectors collapse to one rule, the
        // way the legacy inducer did. The claim chain is non-null when the
        // identity path produced one; identical CSS-string keys are guaranteed
        // to come from the same target shape so the first non-null wins.
        var byRoleSelector = perBlock
            .GroupBy(t => (t.Role, t.CssSelector))
            .ToList();

        var rules = byRoleSelector.Select((g, i) =>
        {
            // Prefer a member with a populated claim chain; if none have one
            // (no-document fallback path, or every block resolved as synthetic
            // /structured-data style XPath), leave Claims null and let the
            // existing CssSelectors string carry the rule.
            var first = g.FirstOrDefault(t => t.Claims is not null);
            return new BlockRule
            {
                RuleId = $"r{i:D4}",
                Role = g.Key.Role,
                CssSelectors = new[] { g.Key.CssSelector },
                Claims = first.Claims,
                MeanConfidence = g.Average(t => t.Confidence),
                ObservationCount = 1,
                DriftScore = 0,
            };
        }).ToList();

        var byRoleCentroid = blocks
            .GroupBy(b => b.Role)
            .ToDictionary(g => g.Key, g => new RoleCentroid
            {
                ObservationCount = g.Count(),
                MeanLinkDensity = g.Average(b => b.LinkDensity),
                MeanTextLength = g.Average(b => b.TextLength),
                MeanDepth = g.Average(b => (double)b.XPath.Count(c => c == '/'))
            });

        return new LearnedExtractor
        {
            TemplateId = templateId,
            Version = 1,
            Rules = rules,
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 1,
                ByRole = byRoleCentroid,
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.UtcNow
            }
        };
    }

    private void EmitForBlock(
        ExtractedBlock block,
        IDocument? document,
        out string cssSelector,
        out IdentityClaim[]? claims,
        out double confidence)
    {
        confidence = block.Confidence;
        claims = null;

        if (document is not null)
        {
            var target = ResolveByXPath(document, block.XPath);
            if (target is not null)
            {
                var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, document, _classFilter);
                claims = result.Chain;
                cssSelector = IdentityClaimSelectorBuilder.ToCssSelector(result.Chain);
                if (result.HitDepthCap)
                {
                    confidence = Math.Max(0.0, block.Confidence - DepthCapConfidencePenalty);
                }
                return;
            }
        }

        // Fallback: legacy XPath-based CSS string. Preserved verbatim so the
        // existing back-compat shape (and ExtractorApplicator's string-CSS
        // path) keeps working until Task 3 swaps the apply path to claims.
        cssSelector = block.CssSelector ?? CssSelectorGeneralizer.Generalize(block.XPath);
    }

    /// <summary>
    /// Resolve a positional XPath back to an <see cref="IElement"/>. Matches
    /// the format produced by <see cref="XPathBuilder.ComputeXPath"/>, which
    /// walks until <see cref="IElement.ParentElement"/> is null and so omits
    /// the document element. Real-world inputs therefore start at
    /// <c>/body[1]/...</c> or <c>/head[1]/...</c>. Returns null on any
    /// parse failure or missing step.
    /// </summary>
    private static IElement? ResolveByXPath(IDocument document, string xpath)
    {
        if (string.IsNullOrEmpty(xpath) || xpath[0] != '/') return null;

        var parts = xpath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return null;

        // Synthetic XPaths used by structured-data + rehydration blocks (e.g.
        // /structured-data, /nextdata-rehydration) can't be resolved against
        // the DOM. Bail and let the fallback CSS path handle them.
        if (parts.Length == 1 && parts[0].IndexOf('[') < 0) return null;

        var docEl = document.DocumentElement;
        if (docEl is null) return null;

        // Choose anchor: if the first part is the document element (e.g.
        // html[1]) start there; otherwise find a top-level child whose name
        // matches (body, head). This covers both XPath shapes our codebase
        // emits.
        IElement? current;
        int startIndex;
        var (firstName, firstIdx) = ParseStep(parts[0]);
        if (firstName is null) return null;
        if (string.Equals(docEl.LocalName, firstName, StringComparison.OrdinalIgnoreCase))
        {
            current = docEl;
            startIndex = 1;
        }
        else
        {
            current = FindNthChildByTag(docEl, firstName, firstIdx);
            startIndex = 1;
            if (current is null) return null;
        }

        for (var i = startIndex; i < parts.Length; i++)
        {
            var (name, idx) = ParseStep(parts[i]);
            if (name is null) return null;
            var next = FindNthChildByTag(current!, name, idx);
            if (next is null) return null;
            current = next;
        }
        return current;
    }

    private static IElement? FindNthChildByTag(IElement parent, string tag, int idx)
    {
        var count = 0;
        foreach (var child in parent.Children)
        {
            if (string.Equals(child.LocalName, tag, StringComparison.OrdinalIgnoreCase))
            {
                count++;
                if (count == idx) return child;
            }
        }
        return null;
    }

    private static (string? Name, int Idx) ParseStep(string part)
    {
        var bracket = part.IndexOf('[');
        if (bracket < 0) return (part, 1);
        var name = part[..bracket];
        var end = part.IndexOf(']', bracket);
        if (end < 0) return (name, 1);
        var num = part.Substring(bracket + 1, end - bracket - 1);
        return int.TryParse(num, out var idx) ? (name, idx) : (name, 1);
    }
}
