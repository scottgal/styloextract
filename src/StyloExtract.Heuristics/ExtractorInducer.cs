using StyloExtract.Abstractions;

namespace StyloExtract.Heuristics;

public sealed class ExtractorInducer : IExtractorInducer
{
    public LearnedExtractor Induce(Guid templateId, IReadOnlyList<ExtractedBlock> blocks)
    {
        var byRoleSelector = blocks
            .GroupBy(b => (b.Role, Selector: b.CssSelector ?? CssSelectorGeneralizer.Generalize(b.XPath)))
            .ToList();

        var rules = byRoleSelector.Select((g, i) => new BlockRule
        {
            RuleId = $"r{i:D4}",
            Role = g.Key.Role,
            CssSelectors = new[] { g.Key.Selector },
            MeanConfidence = g.Average(b => b.Confidence),
            ObservationCount = 1,
            DriftScore = 0
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
}
