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

        var result = applicator.Apply(doc, extractor);

        result.Blocks.Should().HaveCount(2);
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Confidence == 0.92);
        result.Blocks.Should().Contain(b => b.Role == BlockRole.Footer && b.Confidence == 0.88);
        result.RulesApplied.Should().Be(2);
        result.RulesMissed.Should().Be(0);
    }

    [Fact]
    public void Apply_PopulatesMarkdown_OnContentRoles()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.MainContent, CssSelectors = new[] { "main > article" }, MeanConfidence = 0.92, ObservationCount = 5, DriftScore = 0 },
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><main><article><h1>Top</h1><p>See <a href=\"/x\">link</a> for info.</p></article></main></body></html>");

        var result = applicator.Apply(doc, extractor);
        var main = result.Blocks.Single(b => b.Role == BlockRole.MainContent);
        main.Markdown.Should().NotBeNullOrEmpty();
        main.Markdown.Should().Contain("# Top");
        main.Markdown.Should().Contain("[link](/x)");
    }

    [Fact]
    public void Apply_LeavesMarkdownEmpty_OnNonContentRoles()
    {
        IExtractorApplicator applicator = new ExtractorApplicator();
        var extractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule { RuleId = "r0", Role = BlockRole.PrimaryNavigation, CssSelectors = new[] { "nav" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r1", Role = BlockRole.Footer, CssSelectors = new[] { "footer" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
                new BlockRule { RuleId = "r2", Role = BlockRole.Boilerplate, CssSelectors = new[] { "aside" }, MeanConfidence = 0.85, ObservationCount = 5, DriftScore = 0 },
            },
            Centroid = new ExtractorCentroidState { TotalObservations = 5, ByRole = new Dictionary<BlockRole, RoleCentroid>(), OverallDriftScore = 0, LastObservation = DateTimeOffset.UtcNow }
        };
        var doc = new AngleSharpHtmlDomParser().Parse(
            "<html><body><nav><a href='/'>Home</a></nav><aside>related</aside><footer>©</footer></body></html>");

        var result = applicator.Apply(doc, extractor);
        result.Blocks.Should().OnlyContain(b => b.Markdown == "");
    }
}
