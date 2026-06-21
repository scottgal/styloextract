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

public class LayoutExtractorOrchestrationTests
{
    private sealed class CapturingSink : ITemplateVersionEventSink
    {
        public List<NewTemplateEvent> NewEvents { get; } = new();
        public List<VersionChangeEvent> VersionEvents { get; } = new();
        public ValueTask OnNewTemplateAsync(NewTemplateEvent evt, CancellationToken ct) { NewEvents.Add(evt); return ValueTask.CompletedTask; }
        public ValueTask OnVersionChangeAsync(VersionChangeEvent evt, CancellationToken ct) { VersionEvents.Add(evt); return ValueTask.CompletedTask; }
    }

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

    private static (ILayoutExtractor Extractor, SqliteConnection Conn) BuildWithSink(ITemplateVersionEventSink sink)
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
            sink);
        return (extractor, conn);
    }

    [Fact]
    public async Task ExtractAsync_SameHtmlTwice_SecondCallIsFastPathHit()
    {
        var (e, conn) = Build();
        try
        {
            const string html = "<html><body><header><nav class='main-menu'><a href='/'>H</a><a href='/a'>A</a></nav></header>" +
                "<main><article><h1>Title</h1><p>This is a substantial article body with enough text that the heuristic classifier " +
                "will recognise it as MainContent. The paragraph is padded out so total text length comfortably exceeds two hundred " +
                "characters and the link density stays below ten percent throughout this paragraph of actual prose content.</p>" +
                "</article></main></body></html>";
            var uri = new Uri("https://example.com/page");

            var first = await e.ExtractAsync(html, uri);
            first.Match.Status.Should().Be(MatchStatus.Novel);
            first.Match.TemplateId.Should().NotBeNull();

            var second = await e.ExtractAsync(html, uri);
            second.Match.Status.Should().Be(MatchStatus.FastPathHit);
            second.Match.TemplateId.Should().Be(first.Match.TemplateId);
            second.Match.Similarity.Should().BeGreaterThan(0.95);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ExtractAsync_NoLearning_ProducesNovelEphemeral()
    {
        var (e, conn) = Build();
        try
        {
            var html = "<html><body><main><article><p>" +
                "This is a substantial article body with enough text that the heuristic classifier will recognise it as MainContent. " +
                "The paragraph is padded out so the total text length comfortably exceeds two hundred characters and the link density " +
                "stays well below ten percent throughout this paragraph of actual prose content. " +
                new string('x', 300) +
                "</p></article></main></body></html>";
            var uri = new Uri("https://example.com/page");

            var result = await e.ExtractAsync(html, uri, new ExtractionOptions { LearnNewTemplates = false });

            result.Match.Status.Should().Be(MatchStatus.NovelEphemeral);
            result.Match.TemplateId.Should().BeNull();
            result.Markdown.Should().NotBeNullOrWhiteSpace();
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task ExtractAsync_NovelTemplate_FiresOnNewTemplate()
    {
        var sink = new CapturingSink();
        var (e, conn) = BuildWithSink(sink);
        try
        {
            const string html = "<html><body><main><article><p>hello</p></article></main></body></html>";
            await e.ExtractAsync(html, new Uri("https://example.com/x"));
            sink.NewEvents.Should().ContainSingle();
        }
        finally { conn.Dispose(); }
    }
}
