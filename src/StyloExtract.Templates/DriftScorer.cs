using StyloExtract.Abstractions;

namespace StyloExtract.Templates;

public sealed record ApplicationDriftReport
{
    public required double OverallDelta { get; init; }
    public required IReadOnlyDictionary<string, double> PerRuleDelta { get; init; }
}

public static class DriftScorer
{
    public static ApplicationDriftReport ScoreApplication(LearnedExtractor extractor, IReadOnlyList<ExtractedBlock> appliedBlocks)
    {
        int totalRules = extractor.Rules.Count;
        if (totalRules == 0)
        {
            return new ApplicationDriftReport { OverallDelta = 0, PerRuleDelta = new Dictionary<string, double>() };
        }

        var byRole = appliedBlocks.GroupBy(b => b.Role).ToDictionary(g => g.Key, g => g.ToList());
        var perRule = new Dictionary<string, double>();
        int matchedRules = 0;
        double metricSum = 0;
        int metricCount = 0;

        foreach (var rule in extractor.Rules)
        {
            if (!byRole.TryGetValue(rule.Role, out var hits) || hits.Count == 0)
            {
                perRule[rule.RuleId] = 1.0;
                continue;
            }
            matchedRules++;
            if (!extractor.Centroid.ByRole.TryGetValue(rule.Role, out var centroid))
            {
                perRule[rule.RuleId] = 0;
                continue;
            }
            var actualLink = hits.Average(b => b.LinkDensity);
            var actualText = hits.Average(b => b.TextLength);
            var linkDelta = Normalise(Math.Abs(actualLink - centroid.MeanLinkDensity));
            var textDelta = Normalise(Math.Abs(actualText - centroid.MeanTextLength) / Math.Max(1, centroid.MeanTextLength));
            var delta = (linkDelta + textDelta) / 2;
            perRule[rule.RuleId] = delta;
            metricSum += delta;
            metricCount++;
        }

        var unmatched = 1.0 - (double)matchedRules / totalRules;
        var avgMetric = metricCount == 0 ? 0 : metricSum / metricCount;
        var overall = Math.Clamp(unmatched + avgMetric * 0.5, 0, 1);
        return new ApplicationDriftReport { OverallDelta = overall, PerRuleDelta = perRule };
    }

    private static double Normalise(double v) => Math.Min(1.0, v);
}
