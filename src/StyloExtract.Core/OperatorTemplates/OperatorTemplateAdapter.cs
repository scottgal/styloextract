using System.Security.Cryptography;
using System.Text;
using StyloExtract.Abstractions;

namespace StyloExtract.Core.OperatorTemplates;

/// <summary>
/// Converts a parsed <see cref="OperatorTemplate"/> into a synthetic
/// <see cref="LearnedExtractor"/> that the existing
/// <see cref="IExtractorApplicator"/> can run unchanged. The TemplateId is a
/// stable hash of <c>host + version</c> so consumers reading the layout-match
/// result can correlate operator-override responses across requests, and the
/// centroid state is empty (the override path never participates in drift /
/// refit accounting).
/// </summary>
public static class OperatorTemplateAdapter
{
    public static LearnedExtractor ToLearnedExtractor(OperatorTemplate template)
    {
        var rules = new List<BlockRule>(template.Rules.Count);
        for (int i = 0; i < template.Rules.Count; i++)
        {
            var r = template.Rules[i];
            rules.Add(new BlockRule
            {
                RuleId = $"op{i:D4}",
                Role = r.Role,
                CssSelectors = r.Selectors,
                MeanConfidence = r.Confidence,
                ObservationCount = 1,
                DriftScore = 0,
                Claims = r.Claims,
            });
        }
        return new LearnedExtractor
        {
            TemplateId = StableTemplateId(template.Host, template.Version),
            Version = template.Version,
            Rules = rules,
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 0,
                ByRole = new Dictionary<BlockRole, RoleCentroid>(),
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.MinValue,
            },
        };
    }

    private static Guid StableTemplateId(string host, int version)
    {
        // Deterministic GUID per (host, version) so the same operator template
        // surfaces with the same TemplateId across restarts. Uses the first 16
        // bytes of SHA-256("operator|host|version"), interpreted as a Guid.
        Span<byte> hash = stackalloc byte[32];
        var input = Encoding.UTF8.GetBytes($"operator|{host}|{version}");
        SHA256.HashData(input, hash);
        return new Guid(hash[..16]);
    }
}
