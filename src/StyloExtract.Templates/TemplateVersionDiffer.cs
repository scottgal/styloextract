using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;

namespace StyloExtract.Templates;

public static class TemplateVersionDiffer
{
    public static TemplateVersionDiff Diff(
        LearnedExtractor oldEx,
        LearnedExtractor newEx,
        StructuralFingerprint oldFp,
        StructuralFingerprint newFp)
    {
        var oldByRole = oldEx.Rules.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.ToList());
        var newByRole = newEx.Rules.GroupBy(r => r.Role).ToDictionary(g => g.Key, g => g.ToList());

        var added = newEx.Rules.Where(r => !oldByRole.ContainsKey(r.Role)).ToList();
        var removed = oldEx.Rules.Where(r => !newByRole.ContainsKey(r.Role)).ToList();

        var changed = new List<RuleSelectorChange>();
        foreach (var role in oldByRole.Keys.Intersect(newByRole.Keys))
        {
            var oldSelectors = oldByRole[role].SelectMany(r => r.CssSelectors).Distinct().ToList();
            var newSelectors = newByRole[role].SelectMany(r => r.CssSelectors).Distinct().ToList();
            if (!oldSelectors.SequenceEqual(newSelectors))
            {
                changed.Add(new RuleSelectorChange
                {
                    RuleId = oldByRole[role].First().RuleId,
                    Role = role,
                    OldSelectors = oldSelectors,
                    NewSelectors = newSelectors
                });
            }
        }

        var topPq = ComputeTopPqGramDimensions(oldEx, newEx);
        var jaccardDelta = 1.0 - JaccardEstimator.Estimate(oldFp.StructuralMinHash, newFp.StructuralMinHash);

        return new TemplateVersionDiff
        {
            TopChangedDimensions = topPq,
            AddedRules = added,
            RemovedRules = removed,
            ChangedSelectors = changed,
            SignatureJaccardDelta = jaccardDelta
        };
    }

    private static IReadOnlyList<PqGramDimensionChange> ComputeTopPqGramDimensions(LearnedExtractor _, LearnedExtractor __)
    {
        // pq-gram counts are stored on the StructuralFingerprint, not LearnedExtractor.
        // Caller-supplied diff path will need to pass them separately; v1 returns empty list when not available.
        return Array.Empty<PqGramDimensionChange>();
    }
}
