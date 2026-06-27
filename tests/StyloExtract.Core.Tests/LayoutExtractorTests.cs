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

    [Fact]
    public async Task InduceThenApply_ClaimsTemplate_RoundTripsCleanly()
    {
        // End-to-end: first request induces a new template with Claims-populated
        // rules (Task 2). Second request on the same shape hits the fast path,
        // routes through the claim-based applicator (Task 3), and produces
        // the same content. Stability across the round trip is the contract.
        var html = "<html><head><title>Doc</title></head><body>" +
                   "<main id='content'><article><h1>Topic</h1><p>" +
                   new string('x', 400) + "</p></article></main></body></html>";

        var (e, conn) = Build();
        try
        {
            var first = await e.ExtractAsync(html, sourceUri: new Uri("https://example.com/a"),
                options: new ExtractionOptions { LearnNewTemplates = true });
            first.Match.Status.Should().Be(MatchStatus.Novel);

            var second = await e.ExtractAsync(html, sourceUri: new Uri("https://example.com/a"),
                options: new ExtractionOptions { LearnNewTemplates = true });
            second.Match.Status.Should().BeOneOf(MatchStatus.FastPathHit, MatchStatus.Refit);
            second.Markdown.Should().NotBeNullOrWhiteSpace();
            // The applicator-emitted content should align with what the
            // heuristic classifier produced first time round.
            second.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task InduceThenApply_LegacyCssOnlyTemplate_StillExtracts()
    {
        // Legacy back-compat: a template whose rules have Claims=null (the
        // shape persisted before Task 2) must keep applying via the
        // CSS-string evaluator. Construct one explicitly and feed it through
        // the applicator to verify the dispatch fallback.
        var html = "<html><body><main><article>legacy-body</article></main></body></html>";
        var doc = new AngleSharpHtmlDomParser().Parse(html);
        IExtractorApplicator app = new ExtractorApplicator();

        var legacyExtractor = new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = new[]
            {
                new BlockRule
                {
                    RuleId = "r0",
                    Role = BlockRole.MainContent,
                    CssSelectors = new[] { "main > article" },
                    Claims = null,
                    MeanConfidence = 0.8,
                    ObservationCount = 1,
                    DriftScore = 0,
                },
            },
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 1,
                ByRole = new Dictionary<BlockRole, RoleCentroid>(),
                OverallDriftScore = 0,
                LastObservation = DateTimeOffset.UtcNow,
            },
        };

        var result = app.Apply(doc, legacyExtractor);
        result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent && b.Text == "legacy-body");
        result.RulesApplied.Should().Be(1);
    }
}
