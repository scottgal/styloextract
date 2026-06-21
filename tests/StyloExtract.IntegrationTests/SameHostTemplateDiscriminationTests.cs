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

public class SameHostTemplateDiscriminationTests
{
    private static (ILayoutExtractor, SqliteConnection) Build()
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
        return (new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink()), conn);
    }

    [Fact]
    public async Task TwoArticles_SameTemplate_SecondMatchesFirst()
    {
        var (e, conn) = Build();
        try
        {
            var a = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var b = await File.ReadAllTextAsync("Fixtures/example/article-alt.html");
            var uri = new Uri("https://example.com/post");

            var first = await e.ExtractAsync(a, uri);
            var second = await e.ExtractAsync(b, uri);

            first.Match.Status.Should().Be(MatchStatus.Novel);
            second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.SlowPathMatch);
            first.Match.TemplateId.Should().NotBeNull();
            second.Match.TemplateId.Should().NotBeNull();
            second.Match.TemplateId!.Value.Should().Be(first.Match.TemplateId!.Value);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ArticleAndProduct_SameHost_AreDistinctTemplates()
    {
        var (e, conn) = Build();
        try
        {
            var article = await File.ReadAllTextAsync("Fixtures/example/article.html");
            var product = await File.ReadAllTextAsync("Fixtures/example/product.html");
            var uri = new Uri("https://example.com/x");

            var r1 = await e.ExtractAsync(article, uri);
            var r2 = await e.ExtractAsync(product, uri);

            r1.Match.Status.Should().Be(MatchStatus.Novel);
            r2.Match.Status.Should().Be(MatchStatus.Novel);
            r1.Match.TemplateId.Should().NotBeNull();
            r2.Match.TemplateId.Should().NotBeNull();
            r2.Match.TemplateId!.Value.Should().NotBe(r1.Match.TemplateId!.Value);
        }
        finally { conn.Dispose(); }
    }
}
