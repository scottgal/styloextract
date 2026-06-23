using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Templates;

namespace StyloExtract.Performance.Benchmarks;

/// <summary>
/// Times the full extraction pipeline (parse → clean → fingerprint → segment
/// → classify → render) for each fixture. The template index is in-memory
/// SQLite shared across iterations so the second and later invocations of
/// the same template signature follow the fast path. Compare against
/// <see cref="DomMarkdownWalkerBenchmarks"/> to attribute time across the
/// walker vs the rest of the pipeline.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class FullPipelineBenchmarks
{
    private string _small = null!;
    private string _medium = null!;
    private string _large = null!;
    private string _tableHeavy = null!;
    private ILayoutExtractor _extractor = null!;
    private SqliteConnection _conn = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "article-small.html"));
        _medium = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "article-medium.html"));
        _large = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "article-large.html"));
        _tableHeavy = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "table-heavy.html"));

        var cs = $"Data Source=file:bench-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        _conn = new SqliteConnection(cs);
        _conn.Open();
        SqliteSchema.EnsureCreated(_conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        _extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink());

        // Warm the template index so each measured iteration takes the fast path.
        _ = _extractor.ExtractAsync(_small, new Uri("https://example.com/small")).GetAwaiter().GetResult();
        _ = _extractor.ExtractAsync(_medium, new Uri("https://example.com/medium")).GetAwaiter().GetResult();
        _ = _extractor.ExtractAsync(_large, new Uri("https://example.com/large")).GetAwaiter().GetResult();
        _ = _extractor.ExtractAsync(_tableHeavy, new Uri("https://example.com/table")).GetAwaiter().GetResult();
    }

    [GlobalCleanup]
    public void Cleanup() => _conn?.Dispose();

    [Benchmark(Baseline = true)]
    public async Task<ExtractionResult> Extract_Article_Small()
        => await _extractor.ExtractAsync(_small, new Uri("https://example.com/small"));

    [Benchmark]
    public async Task<ExtractionResult> Extract_Article_Medium()
        => await _extractor.ExtractAsync(_medium, new Uri("https://example.com/medium"));

    [Benchmark]
    public async Task<ExtractionResult> Extract_Article_Large()
        => await _extractor.ExtractAsync(_large, new Uri("https://example.com/large"));

    [Benchmark]
    public async Task<ExtractionResult> Extract_TableHeavy()
        => await _extractor.ExtractAsync(_tableHeavy, new Uri("https://example.com/table"));
}
