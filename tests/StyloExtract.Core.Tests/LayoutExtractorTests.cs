using FluentAssertions;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using Xunit;

namespace StyloExtract.Core.Tests;

public class LayoutExtractorTests
{
    private static ILayoutExtractor Build()
    {
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
        return new LayoutExtractor(
            new AngleSharpHtmlDomParser(),
            new DomCleaner(),
            fingerprinter,
            new BlockSegmenter(),
            HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer());
    }

    [Fact]
    public async Task ExtractAsync_ProducesNovelEphemeralResultWithMarkdown()
    {
        var html = "<html><head><title>Test</title></head><body><main><article><p>" +
                   new string('x', 300) + "</p></article></main></body></html>";

        var result = await Build().ExtractAsync(html);

        result.Match.Status.Should().Be(MatchStatus.NovelEphemeral);
        result.Match.TemplateId.Should().BeNull();
        result.Title.Should().Be("Test");
        result.Markdown.Should().NotBeNullOrWhiteSpace();
        result.Blocks.Should().NotBeEmpty();
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
    }

    [Fact]
    public async Task ExtractAsync_PopulatesFingerprintHex()
    {
        var html = "<html><body><main><article><p>" +
                   new string('x', 300) + "</p></article></main></body></html>";
        var result = await Build().ExtractAsync(html);
        result.Match.FingerprintHex.Should().NotBeNullOrEmpty();
        result.Stats.FingerprintShingleCount.Should().BeGreaterThan(0);
    }
}
