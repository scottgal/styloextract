using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Core.Tests;

public class LayoutExtractorTests
{
    private static (ILayoutExtractor Extractor, SqliteConnection Conn) Build()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise),
            sketcher,
            new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher),
            new PqGramExtractor());
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(),
            new DomCleaner(),
            fingerprinter,
            new BlockSegmenter(),
            HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(),
            index,
            new HostHasher(new byte[32]),
            new ExtractorInducer(),
            new ExtractorApplicator(),
            fastPathThreshold: 0.85,
            slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink());
        return (extractor, conn);
    }

    [Fact]
    public async Task ExtractAsync_ProducesNovelEphemeralResultWithMarkdown()
    {
        var html = "<html><head><title>Test</title></head><body><main><article><p>" +
                   new string('x', 300) + "</p></article></main></body></html>";

        var (e, conn) = Build();
        try
        {
            var result = await e.ExtractAsync(html, options: new ExtractionOptions { LearnNewTemplates = false });

            result.Match.Status.Should().Be(MatchStatus.NovelEphemeral);
            result.Match.TemplateId.Should().BeNull();
            result.Title.Should().Be("Test");
            result.Markdown.Should().NotBeNullOrWhiteSpace();
            result.Blocks.Should().NotBeEmpty();
            result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ExtractAsync_PopulatesFingerprintHex()
    {
        var html = "<html><body><main><article><p>" +
                   new string('x', 300) + "</p></article></main></body></html>";

        var (e, conn) = Build();
        try
        {
            var result = await e.ExtractAsync(html, options: new ExtractionOptions { LearnNewTemplates = false });
            result.Match.FingerprintHex.Should().NotBeNullOrEmpty();
            result.Stats.FingerprintShingleCount.Should().BeGreaterThan(0);
        }
        finally { conn.Dispose(); }
    }
}
