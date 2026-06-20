using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class TemplateVersionDifferTests
{
    private static LearnedExtractor Ex(params (BlockRole role, string sel)[] rules) => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = rules.Select((r, i) => new BlockRule
        {
            RuleId = $"r{i}",
            Role = r.role,
            CssSelectors = new[] { r.sel },
            MeanConfidence = 0.9,
            ObservationCount = 1,
            DriftScore = 0
        }).ToList(),
        Centroid = new ExtractorCentroidState { TotalObservations = 1, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
    };

    private static StructuralFingerprint Fp(uint seed)
    {
        var sig = new uint[128]; Array.Fill(sig, seed);
        return new StructuralFingerprint
        {
            StructuralMinHash = sig, AnchorMinHash = sig, LshBands = new ulong[16],
            PqGramCounts = new Dictionary<string, double>(), PqGramNorm = 0, ShingleCount = 1, Hex = ""
        };
    }

    [Fact]
    public void Diff_DetectsAddedAndRemovedRules()
    {
        var oldEx = Ex((BlockRole.MainContent, "main"), (BlockRole.Footer, "footer"));
        var newEx = Ex((BlockRole.MainContent, "main"), (BlockRole.PrimaryNavigation, "nav"));

        var diff = TemplateVersionDiffer.Diff(oldEx, newEx, Fp(1), Fp(2));

        diff.AddedRules.Should().ContainSingle(r => r.Role == BlockRole.PrimaryNavigation);
        diff.RemovedRules.Should().ContainSingle(r => r.Role == BlockRole.Footer);
    }
}
