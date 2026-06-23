using AngleSharp;
using AngleSharp.Dom;
using BenchmarkDotNet.Attributes;
using StyloExtract.Heuristics;

namespace StyloExtract.Performance.Benchmarks;

/// <summary>
/// Benchmarks the DOM walk in isolation: parsing happens once during
/// <c>GlobalSetup</c>, the timed code path is just the walker producing
/// markdown for the article body element. This isolates the new code path
/// from the rest of the pipeline so allocation patterns and walk-depth
/// effects surface cleanly.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 10)]
public class DomMarkdownWalkerBenchmarks
{
    private IElement _small = null!;
    private IElement _medium = null!;
    private IElement _large = null!;
    private IElement _tableHeavy = null!;

    [GlobalSetup]
    public void Setup()
    {
        _small = LoadBody("article-small.html");
        _medium = LoadBody("article-medium.html");
        _large = LoadBody("article-large.html");
        _tableHeavy = LoadBody("table-heavy.html");
    }

    private static IElement LoadBody(string name)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        var html = File.ReadAllText(path);
        var ctx = BrowsingContext.New(Configuration.Default);
        var doc = ctx.OpenAsync(req => req.Content(html)).GetAwaiter().GetResult();
        // The walker is normally invoked per-block; for an isolated benchmark we walk
        // the body element so each scenario covers the full content surface.
        return doc.Body ?? doc.DocumentElement;
    }

    [Benchmark(Baseline = true)]
    public string Walker_Article_Small() => DomMarkdownWalker.Render(_small);

    [Benchmark]
    public string Walker_Article_Medium() => DomMarkdownWalker.Render(_medium);

    [Benchmark]
    public string Walker_Article_Large() => DomMarkdownWalker.Render(_large);

    [Benchmark]
    public string Walker_TableHeavy() => DomMarkdownWalker.Render(_tableHeavy);
}
