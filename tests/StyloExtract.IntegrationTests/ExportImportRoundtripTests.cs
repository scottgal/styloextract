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

namespace StyloExtract.IntegrationTests;

public class ExportImportRoundtripTests
{
    [Fact]
    public async Task Export_Import_PreservesMatch()
    {
        var (e1, conn1) = BuildExtractor();
        try
        {
            var html = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var uri = new Uri("https://example.com/post");

            await e1.ExtractAsync(html, uri); // Novel → registers

            // Export host
            var host = new HostHasher(new byte[32]).Hash("example.com");
            using var ms = new MemoryStream();
            await TemplateExporter.ExportHostAsync(conn1, host, "example.com", ms, default);
            ms.Position = 0;

            // Import into a fresh DB-backed extractor
            var (e2, conn2) = BuildExtractor();
            try
            {
                var importResult = await TemplateImporter.ImportAsync(conn2, host, ms, default);
                importResult.ImportedCount.Should().Be(1);

                var second = await e2.ExtractAsync(html, uri);
                second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
            }
            finally { conn2.Dispose(); }
        }
        finally { conn1.Dispose(); }
    }

    private static (ILayoutExtractor, SqliteConnection) BuildExtractor()
    {
        var cs = $"Data Source=file:testdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var refit = new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3);
        return (new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            refit, new DefaultNoopVersionEventSink()), conn);
    }
}
