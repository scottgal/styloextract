using BenchmarkDotNet.Attributes;
using Microsoft.Data.Sqlite;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Templates;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class FastPathMatchBench
{
    private SqliteConnection _conn = null!;
    private SqliteTemplateIndex _index = null!;
    private StructuralFingerprint _fp = null!;
    private byte[] _host = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        _conn = new SqliteConnection("Data Source=:memory:");
        _conn.Open();
        SqliteSchema.EnsureCreated(_conn);
        _index = new SqliteTemplateIndex(_conn);
        var parser = new AngleSharpHtmlDomParser();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var doc = parser.Parse("<html><body><main><article><h1>x</h1><p>y</p></article></main></body></html>");
        _fp = fingerprinter.Compute(doc);
        _host = new byte[16];
        var ex = new ExtractorInducer().Induce(Guid.NewGuid(), new[]
        {
            new ExtractedBlock { Id = "b0", Role = BlockRole.MainContent, Confidence = 0.9, Text = "", Markdown = "", XPath = "/", CssSelector = "main > article", TextLength = 100, LinkDensity = 0, Links = Array.Empty<ExtractedLink>() }
        });
        await _index.RegisterAsync(_host, _fp, ex, default);
    }

    [Benchmark]
    public async Task<Guid?> ProbeFastPath() => await _index.ProbeFastPathAsync(_host, _fp, 0.85, default);
}
