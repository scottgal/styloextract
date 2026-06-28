using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Abstractions.TemplateEnrichment;
using StyloExtract.Core;
using StyloExtract.Core.TemplateEnrichment;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// Pins Move 3 of the apply-time-quality-gate spec: when an existing template
/// produces noisy MainContent (Move 2 trips <c>applicatorBugOut</c>) the
/// repair-enqueue gate fires WITHOUT requiring a hand-authored operator
/// template. The original gate required an operator override to exist, which
/// made the LLM repair path unreachable for self-induced templates that went
/// bad.
/// </summary>
public class AutoRepairOnNoisyOutputTests : IDisposable
{
    private readonly SqliteConnection _conn;
    private readonly string _cs;

    public AutoRepairOnNoisyOutputTests()
    {
        _cs = $"Data Source=file:autorepair-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        _conn = new SqliteConnection(_cs);
        _conn.Open();
        SqliteSchema.EnsureCreated(_conn);
    }

    public void Dispose() => _conn.Dispose();

    private LayoutExtractor BuildExtractor(ITemplateEnrichmentQueue queue)
    {
        var index = new SqliteTemplateIndex(_cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());

        return new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink(),
            enrichmentQueue: queue);
    }

    private static async Task<List<TemplateEnrichmentJob>> DrainAsync(ITemplateEnrichmentQueue queue, TimeSpan timeout)
    {
        var jobs = new List<TemplateEnrichmentJob>();
        using var cts = new CancellationTokenSource(timeout);
        try
        {
            await foreach (var job in queue.DequeueAllAsync(cts.Token))
                jobs.Add(job);
        }
        catch (OperationCanceledException) { }
        return jobs;
    }

    [Fact]
    public async Task NoisyMainContent_On_FastPathHit_Enqueues_Repair_Without_OperatorTemplate()
    {
        // Per-host cooldown set to zero so we can observe both the visit-1
        // Induce enqueue AND the visit-2 Repair enqueue inside one test run.
        using var queue = new InMemoryTemplateEnrichmentQueue(
            new EnrichmentQueueOptions { PerHostCooldown = TimeSpan.Zero });

        var extractor = BuildExtractor(queue);
        var host = new Uri("https://autorepair-host.example/page");

        // Both visits use the SAME structural shape: 30 <a> nodes inside <p>
        // inside <main>, plus header/nav/footer. The structural fingerprint
        // sees identical (tag, nth, classHash) tuples either way, so visit 2
        // matches visit 1's template via the fast or slow path. What DIFFERS
        // is the link-text-vs-prose ratio inside the <a>s + surrounding text:
        //   Visit 1 has long prose paragraphs between short links
        //     → link density ~0.05, clean.
        //   Visit 2 has long links with minimal between-text
        //     → link density ~0.95, noisy.
        const int LinkCount = 30;
        var cleanProse = string.Concat(Enumerable.Repeat(
            "This is a paragraph of substantial article body text. ",
            12));
        var cleanInner = string.Concat(Enumerable.Range(0, LinkCount).Select(i =>
            $"{cleanProse} <a href='/x/{i}'>l</a> "));
        var cleanHtml = "<html><body>" +
            "<header><nav><a href='/'>Home</a><a href='/a'>A</a></nav></header>" +
            "<main><h1>Article Title</h1><p>" + cleanInner + "</p></main>" +
            "<footer>copyright 2026</footer>" +
            "</body></html>";
        var visit1 = await extractor.ExtractAsync(
            cleanHtml, host, new ExtractionOptions { LearnNewTemplates = true });
        visit1.Match.Status.Should().Be(MatchStatus.Novel,
            "visit 1 must learn a template so visit 2 can hit it");

        // Visit 2: same shape, 30 <a> nodes, but the body is now mostly link
        // text — language-picker leak shape.
        var noisyInner = string.Concat(Enumerable.Range(0, LinkCount).Select(i =>
            $"<a href='/x/{i}'>Long link number {i} with several words following here for the gate to count</a> "));
        var noisyHtml = "<html><body>" +
            "<header><nav><a href='/'>Home</a><a href='/a'>A</a></nav></header>" +
            "<main><h1>Index Page</h1><p>" + noisyInner + "</p></main>" +
            "<footer>copyright 2026</footer>" +
            "</body></html>";
        var visit2 = await extractor.ExtractAsync(
            noisyHtml, host, new ExtractionOptions { LearnNewTemplates = true });

        visit2.Match.Status.Should().BeOneOf(
            new[] { MatchStatus.FastPathHit, MatchStatus.SlowPathMatch, MatchStatus.Refit },
            "visit 2's structural fingerprint should match the visit-1 template via fast or slow path");

        var jobs = await DrainAsync(queue, TimeSpan.FromMilliseconds(300));

        jobs.Should().Contain(j => j.Kind == EnrichmentJobKind.Repair,
            "Move 3: a noisy-output bug-out on a fast/slow path hit must enqueue a Repair job " +
            "even with no hand-authored operator template present");
    }

    [Fact]
    public async Task Clean_FastPathHit_Does_Not_Enqueue_Repair()
    {
        // Regression: a normal fast-path hit on a clean page must NOT trigger
        // a repair enqueue. The gate fires on applicatorBugOut OR markdown-too-
        // short, neither of which is true for clean output.
        using var queue = new InMemoryTemplateEnrichmentQueue(
            new EnrichmentQueueOptions { PerHostCooldown = TimeSpan.Zero });

        var extractor = BuildExtractor(queue);
        var host = new Uri("https://clean-host.example/page");

        var prose = string.Concat(Enumerable.Repeat(
            "This is a paragraph of substantial article body text that the heuristic classifier recognises as MainContent. ",
            12));
        var html = "<html><body>" +
            "<header><nav><a href='/'>Home</a><a href='/a'>A</a></nav></header>" +
            "<main><h1>Article Title</h1><p>" + prose + "</p></main>" +
            "<footer>copyright 2026</footer>" +
            "</body></html>";

        // Same bytes for both visits so the structural fingerprint is identical.
        await extractor.ExtractAsync(html, host, new ExtractionOptions { LearnNewTemplates = true });
        var visit2 = await extractor.ExtractAsync(html, host, new ExtractionOptions { LearnNewTemplates = true });
        visit2.Match.Status.Should().BeOneOf(
            new[] { MatchStatus.FastPathHit, MatchStatus.SlowPathMatch });

        var jobs = await DrainAsync(queue, TimeSpan.FromMilliseconds(200));

        jobs.Should().NotContain(j => j.Kind == EnrichmentJobKind.Repair,
            "clean fast-path hits must not enqueue repair");
    }
}