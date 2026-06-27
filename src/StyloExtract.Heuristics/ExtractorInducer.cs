using AngleSharp.Dom;
using Microsoft.Extensions.Logging;
using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorInducer : IExtractorInducer
{
    private readonly IClassStabilityFilter _classFilter;
    private readonly ILogger<ExtractorInducer>? _logger;

    // Task 51 / 2.1: hard uniqueness postcondition. When the identity-claim
    // builder hits its ancestor-depth cap without becoming unique, we force
    // the rule's confidence to exactly 0.0 (was previously -0.1 soft penalty).
    // A non-unique chain at induction time will pick arbitrary elements at
    // apply time — confidence == 0.0 IS the "requires_review" marker.
    private const double NonUniqueChainConfidence = 0.0;

    public ExtractorInducer() : this(new DefaultClassStabilityFilter(), logger: null) { }

    public ExtractorInducer(IClassStabilityFilter classFilter) : this(classFilter, logger: null) { }

    public ExtractorInducer(IClassStabilityFilter classFilter, ILogger<ExtractorInducer>? logger)
    {
        _classFilter = classFilter;
        _logger = logger;
    }

    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks) =>
        Induce(templateId, blocks, document: null);

    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks, IDocument? document)
    {
        // Per-block emit: when a document is available, route through the
        // identity-claim builder; otherwise fall back to the legacy XPath →
        // CSS-string generalizer (kept for callers like RefitOrchestrator that
        // don't currently plumb the document).
        //
        // Task 52 / cardinality-aware uniqueness: blocks that share a repeated
        // role (RepeatedItem, etc.) are emitted together — one identity-claim
        // chain that matches the WHOLE set of K targets, not K independent
        // single-target chains. Singleton roles still flow through the
        // per-target builder unchanged.
        var perBlock = new (BlockRole Role, string CssSelector, IdentityClaim[]? Claims, double Confidence)[blocks.Count];

        if (document is not null)
        {
            // Group block indices by role so we can dispatch repeated roles in
            // one shot. Preserve original block order inside each group so any
            // downstream collapse stays deterministic.
            var byRole = new Dictionary<BlockRole, List<int>>();
            for (var i = 0; i < blocks.Count; i++)
            {
                if (!byRole.TryGetValue(blocks[i].Role, out var bucket))
                {
                    bucket = new List<int>();
                    byRole[blocks[i].Role] = bucket;
                }
                bucket.Add(i);
            }

            foreach (var (role, indices) in byRole)
            {
                if (RoleCardinality.IsSingleton(role) || indices.Count == 1)
                {
                    // Singleton role OR only one block of a "repeated" role on
                    // this page (degenerate K=1) → existing single-target path.
                    foreach (var i in indices)
                    {
                        EmitForBlock(blocks[i], document, out var css, out var claims, out var confidence);
                        perBlock[i] = (blocks[i].Role, css, claims, confidence);
                    }
                    continue;
                }

                // Repeated role with K > 1: resolve each block's XPath to an
                // IElement and feed the whole set to BuildForRepeatedRole. Any
                // block whose XPath fails to resolve drops to the per-block
                // fallback so we don't lose it from the rule set.
                var targets = new List<IElement>(indices.Count);
                var resolvedIndices = new List<int>(indices.Count);
                var unresolved = new List<int>();
                foreach (var i in indices)
                {
                    var el = ResolveByXPath(document, blocks[i].XPath);
                    if (el is null) { unresolved.Add(i); continue; }
                    targets.Add(el);
                    resolvedIndices.Add(i);
                }

                if (targets.Count >= 2)
                {
                    var result = IdentityClaimSelectorBuilder.BuildForRepeatedRole(
                        targets, document, _classFilter, _logger);
                    var sharedClaims = result.Chain;
                    var sharedCss = IdentityClaimSelectorBuilder.ToCssSelector(sharedClaims);

                    for (var k = 0; k < resolvedIndices.Count; k++)
                    {
                        var i = resolvedIndices[k];
                        var confidence = blocks[i].Confidence;
                        if (result.HitDepthCap)
                        {
                            // Same postcondition Task 51 set for singletons:
                            // non-unique chain → zero confidence requires-review
                            // marker. Applies to the whole repeated set since
                            // they all share one chain.
                            confidence = NonUniqueChainConfidence;
                        }
                        perBlock[i] = (blocks[i].Role, sharedCss, sharedClaims, confidence);
                    }
                }
                else
                {
                    // Fewer than 2 resolved targets — degenerate into the
                    // per-block path for whatever did resolve.
                    foreach (var i in resolvedIndices)
                    {
                        EmitForBlock(blocks[i], document, out var css, out var claims, out var confidence);
                        perBlock[i] = (blocks[i].Role, css, claims, confidence);
                    }
                }

                foreach (var i in unresolved)
                {
                    EmitForBlock(blocks[i], document, out var css, out var claims, out var confidence);
                    perBlock[i] = (blocks[i].Role, css, claims, confidence);
                }
            }
        }
        else
        {
            // No document available → legacy per-block fallback for every role.
            for (var i = 0; i < blocks.Count; i++)
            {
                EmitForBlock(blocks[i], document, out var css, out var claims, out var confidence);
                perBlock[i] = (blocks[i].Role, css, claims, confidence);
            }
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
                var result = IdentityClaimSelectorBuilder.BuildAncestorChain(target, document, _classFilter, _logger);
                claims = result.Chain;
                cssSelector = IdentityClaimSelectorBuilder.ToCssSelector(result.Chain);
                if (result.HitDepthCap)
                {
                    // Hard postcondition: non-unique chain → zero confidence.
                    // This is the "requires_review" marker downstream consumers
                    // should check before applying the rule.
                    confidence = NonUniqueChainConfidence;
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
