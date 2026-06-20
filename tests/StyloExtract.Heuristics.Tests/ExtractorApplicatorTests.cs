using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorApplicatorTests
{
    [Fact]
    public void Apply_EmitsBlocksMatchingRuleSelectors()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.92, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.88, ObservationCount = 5, DriftScore = 0 }
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse("<html><body><main><article>x</article></main><footer>©</footer></body></html>");

        var blocks = applicator.Apply(doc, extractor);

        blocks.Should().HaveCount(2);
        blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Confidence == 0.92);
        blocks.Should().Contain(b => b.Role == BlockRole.Footer && b.Confidence == 0.88);
    }
}
