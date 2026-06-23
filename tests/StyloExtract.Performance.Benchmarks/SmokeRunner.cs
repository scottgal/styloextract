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

// Not a benchmark — a smoke runner that dumps the rendered markdown for each
// fixture so we can evaluate output quality from the AI-scraper's seat.
// Invoke via:  dotnet run --project tests/StyloExtract.Performance.Benchmarks -- --dump
public static class SmokeRunner
{
    public static async Task RunAsync()
    {
        var fixtures = new[] { "article-small.html", "article-medium.html", "article-large.html", "table-heavy.html" };
        var cs = $"Data Source=file:smoke-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        using var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        var extractor = new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink());

        foreach (var name in fixtures)
        {
            var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
            var html = await File.ReadAllTextAsync(path);
            var result = await extractor.ExtractAsync(html, new Uri($"https://example.com/{name}"));
            Console.WriteLine("=================================================================");
            Console.WriteLine($"FIXTURE: {name}  (html: {html.Length} bytes  blocks: {result.Blocks.Count})");
            Console.WriteLine("=================================================================");
            Console.WriteLine(result.Markdown);
            Console.WriteLine();
        }
    }
}
