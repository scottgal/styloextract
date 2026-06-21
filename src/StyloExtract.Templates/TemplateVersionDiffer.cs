using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;

namespace StyloExtract.Templates;

public static class TemplateVersionDiffer
{
    public static TemplateVersionDiff Diff(
        LearnedExtractor oldEx,
        LearnedExtractor newEx,
        StructuralFingerprint oldFp,
        StructuralFingerprint newFp,
        IReadOnlyDictionary<string, double>? oldPqGramCounts = null,
        IReadOnlyDictionary<string, double>? newPqGramCounts = null)
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

        var topPq = ComputeTopPqGramDimensions(
            oldPqGramCounts ?? oldFp.PqGramCounts,
            newPqGramCounts ?? newFp.PqGramCounts);
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

    private static IReadOnlyList<PqGramDimensionChange> ComputeTopPqGramDimensions(
        IReadOnlyDictionary<string, double> oldCounts,
        IReadOnlyDictionary<string, double> newCounts)
    {
        // Union keys from both old and new pq-gram dictionaries, sort by absolute difference, take top 10.
        var allKeys = new HashSet<string>(oldCounts.Keys);
        allKeys.UnionWith(newCounts.Keys);

        return allKeys
            .Select(k =>
            {
                oldCounts.TryGetValue(k, out var oldVal);
                newCounts.TryGetValue(k, out var newVal);
                return (Key: k, OldVal: oldVal, NewVal: newVal, AbsDelta: Math.Abs(newVal - oldVal));
            })
            .OrderByDescending(x => x.AbsDelta)
            .Take(10)
            .Select(x => new PqGramDimensionChange { PqGramKey = x.Key, OldCount = x.OldVal, NewCount = x.NewVal })
            .ToList();
    }
}
