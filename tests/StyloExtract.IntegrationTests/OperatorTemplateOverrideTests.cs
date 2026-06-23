using FluentAssertions;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Core.OperatorTemplates;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.IntegrationTests;

/// <summary>
/// Integration tests for the operator-template hard-override path. Build a
/// <see cref="LayoutExtractor"/> with an <see cref="IOperatorTemplateStore"/>
/// containing one template for <c>operator.example</c>; assert that requests
/// for that host use the operator selectors and that requests for any other
/// host fall through to the induced (heuristic) pipeline unchanged.
/// </summary>
public class OperatorTemplateOverrideTests
{
    private sealed class StubStore : IOperatorTemplateStore
    {
        private readonly Dictionary<string, OperatorTemplate> _byHost = new(StringComparer.OrdinalIgnoreCase);
        public StubStore Add(OperatorTemplate t) { _byHost[t.Host] = t; return this; }
        public bool TryGet(string host, out OperatorTemplate template) => _byHost.TryGetValue(host, out template!);
        public IReadOnlyList<OperatorTemplate> List() => _byHost.Values.ToList();
    }

    private static (ILayoutExtractor, SqliteConnection) Build(IOperatorTemplateStore? store)
    {
        var cs = $"Data Source=file:opdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
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
            new DefaultNoopVersionEventSink(),
            operatorTemplates: store), conn);
    }

    private const string HtmlWithCustomWrapper = """
        <!DOCTYPE html>
        <html><head><title>x</title></head>
        <body>
          <header><nav><a href="/">Home</a></nav></header>
          <div class="totally-custom-content">
            <h1>Operator-owned title</h1>
            <p>This article body lives behind a wrapper class the heuristic classifier doesn't recognise. The operator template knows where the content lives, so the heuristic fallback should never run for this host.</p>
            <p>A second paragraph providing additional body length so the extracted block clears the renderer's 40-char quality gate when emitted.</p>
          </div>
          <footer>copyright 2026</footer>
        </body></html>
        """;

    private static readonly OperatorTemplate OperatorExampleTemplate = new()
    {
        Host = "operator.example",
        Description = "test fixture",
        Version = 1,
        Rules = new[]
        {
            new OperatorTemplateRule
            {
                Role = BlockRole.MainContent,
                Selectors = new[] { ".totally-custom-content" },
                Confidence = 0.95,
            },
        },
    };

    [Fact]
    public async Task Override_Fires_For_Host_With_Operator_Template()
    {
        var store = new StubStore().Add(OperatorExampleTemplate);
        var (e, conn) = Build(store);
        try
        {
            var result = await e.ExtractAsync(HtmlWithCustomWrapper, new Uri("https://operator.example/page"));
            result.Match.Status.Should().Be(MatchStatus.OperatorOverride);
            result.Blocks.Should().ContainSingle(b => b.Role == BlockRole.MainContent);
            result.Markdown.Should().Contain("# Operator-owned title");
            result.Markdown.Should().Contain("This article body lives behind a wrapper class");
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Override_Skips_Fingerprint_And_Index()
    {
        var store = new StubStore().Add(OperatorExampleTemplate);
        var (e, conn) = Build(store);
        try
        {
            var result = await e.ExtractAsync(HtmlWithCustomWrapper, new Uri("https://operator.example/page"));
            // The override path zeroes out FingerprintHex and FingerprintTime / MatchTime
            // for the fingerprint-and-probe stages it skipped. ParseTime is the only
            // non-zero pipeline cost.
            result.Match.FingerprintHex.Should().BeEmpty();
            result.Stats.FingerprintTime.Should().Be(TimeSpan.Zero);
            result.Stats.ParseTime.Should().BeGreaterThan(TimeSpan.Zero);
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task NonOverride_Host_Falls_Through_To_Induced_Pipeline()
    {
        var store = new StubStore().Add(OperatorExampleTemplate);
        var (e, conn) = Build(store);
        try
        {
            // Different host -> store miss -> induced path.
            var result = await e.ExtractAsync(HtmlWithCustomWrapper, new Uri("https://different.example/page"));
            result.Match.Status.Should().NotBe(MatchStatus.OperatorOverride);
            // The induced classifier will not recognise the .totally-custom-content
            // wrapper, so MainContent may not survive — what matters is that the
            // override path didn't fire.
        }
        finally { conn.Dispose(); }
    }

    [Fact]
    public async Task Override_Path_Raises_MatchOperatorOverride_Signal()
    {
        // Regression for the 2026-06-24 architectural review: the override branch
        // used to return after ParseDone without raising any further signal,
        // making operator-template extractions look like hung requests to
        // ephemeral consumers (dashboard, metrics).
        var store = new StubStore().Add(OperatorExampleTemplate);
        var sink = new Mostlylucid.Ephemeral.TypedSignalSink<StyloExtractSignal>();
        var fired = new System.Collections.Concurrent.ConcurrentBag<string>();
        sink.TypedSignalRaised += ev => fired.Add(ev.Signal);

        var (e, conn) = BuildWithSink(store, sink);
        try
        {
            await e.ExtractAsync(HtmlWithCustomWrapper, new Uri("https://operator.example/page"));
            fired.Should().Contain(StyloExtractSignals.MatchOperatorOverride,
                because: "ephemeral consumers must be able to count operator-override matches");
            fired.Should().Contain(StyloExtractSignals.ParseDone);
            fired.Should().NotContain(StyloExtractSignals.FingerprintComputed,
                because: "the override path correctly skips fingerprint, and that must be visible by the absence of the signal");
        }
        finally { conn.Dispose(); }
    }

    private static (ILayoutExtractor, SqliteConnection) BuildWithSink(
        IOperatorTemplateStore store,
        Mostlylucid.Ephemeral.TypedSignalSink<StyloExtractSignal> sink)
    {
        var cs = $"Data Source=file:opdb-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
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
            new DefaultNoopVersionEventSink(),
            signals: sink,
            operatorTemplates: store), conn);
    }

    [Fact]
    public async Task No_Store_Configured_Means_Existing_Pipeline_Behaviour_Unchanged()
    {
        // Null store = backwards-compatible. Every existing consumer that didn't
        // pass operatorTemplates: ... continues to work.
        var (e, conn) = Build(store: null);
        try
        {
            var result = await e.ExtractAsync(HtmlWithCustomWrapper, new Uri("https://operator.example/page"));
            result.Match.Status.Should().NotBe(MatchStatus.OperatorOverride);
        }
        finally { conn.Dispose(); }
    }
}
