using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Templates.Tests;

public class DriftScorerTests
{
    private static LearnedExtractor Make() => new()
    {
        TemplateId = Guid.NewGuid(),
        Version = 1,
        Rules = new[]
        {
            new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.9, ObservationCount = 10, DriftScore = 0 },
            new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.9, ObservationCount = 10, DriftScore = 0 }
        },
        Centroid = new ExtractorCentroidState
        {
            TotalObservations = 10,
            ByRole = new Dictionary<BlockRole, RoleCentroid>
            {
                [BlockRole.MainContent] = new() { ObservationCount = 10, MeanLinkDensity = 0.05, MeanTextLength = 500, MeanDepth = 4 },
                [BlockRole.Footer] = new() { ObservationCount = 10, MeanLinkDensity = 0.4, MeanTextLength = 60, MeanDepth = 2 }
            },
            OverallDriftScore = 0,
            LastObservation = DateTimeOffset.UtcNow
        }
    };

    private static ExtractedBlock Block(BlockRole role, int textLen, double linkDensity) => new()
    {
        Id = "b", Role = role, Confidence = 0.9, Text = "", Markdown = "",
        XPath = "/", CssSelector = "", TextLength = textLen, LinkDensity = linkDensity,
        Links = Array.Empty<ExtractedLink>()
    };

    [Fact]
    public void ScoreApplication_AllRulesMatchAndCentroidsAgree_ProducesLowDrift()
    {
        var report = DriftScorer.ScoreApplication(Make(), new[]
        {
            Block(BlockRole.MainContent, 500, 0.05),
            Block(BlockRole.Footer, 60, 0.4)
        });
        report.OverallDelta.Should().BeLessThan(0.15);
    }

    [Fact]
    public void ScoreApplication_OneRuleMissed_ProducesHighDrift()
    {
        var report = DriftScorer.ScoreApplication(Make(), new[]
        {
            Block(BlockRole.MainContent, 500, 0.05)
        });
        report.OverallDelta.Should().BeGreaterThan(0.4);
    }
}
