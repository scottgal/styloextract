using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Heuristics;
using Xunit;

namespace StyloExtract.Heuristics.Tests;

public class ExtractorInducerTests
{
    [Fact]
    public void Induce_ProducesOneRulePerRoleCssPair()
    {
        IExtractorInducer inducer = new ExtractorInducer();
        var blocks = new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "", Markdown = "", XPath = "/html/body/main/article", CssSelector = "main > article", TextLength = 500, LinkDensity = 0.05, Links = Array.Empty<ExtractedLink>() },
            new ExtractedBlock { Id = "b1", Role = BlockRole.PrimaryNavigation, Confidence = 0.95, Text = "", Markdown = "", XPath = "/html/body/header/nav", CssSelector = "header > nav", TextLength = 50, LinkDensity = 0.9, Links = Array.Empty<ExtractedLink>() }
        };

        var id = Guid.NewGuid();
        var extractor = inducer.Induce(id, blocks);

        extractor.TemplateId.Should().Be(id);
        extractor.Version.Should().Be(1);
        extractor.Rules.Should().HaveCount(2);
        extractor.Rules.Select(r => r.Role).Should().BeEquivalentTo(new[] { BlockRole.MainContent, BlockRole.PrimaryNavigation });
        extractor.Centroid.TotalObservations.Should().Be(1);
    }
}
