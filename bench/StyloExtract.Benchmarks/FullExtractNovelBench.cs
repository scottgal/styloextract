using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

/// <summary>
/// Measures ExtractAsync on a URL never seen before (forces novel registration).
/// Spec §13 target: Novel path &lt;= 50ms p99.
/// </summary>
[MemoryDiagnoser]
public class FullExtractNovelBench
{
    private ILayoutExtractor _extractor = null!;
    private string _html = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        _extractor = sp.GetRequiredService<ILayoutExtractor>();
        _html = File.ReadAllText("article.html");
        _counter = 0;
    }

    [Benchmark]
    public async Task<ExtractionResult> FullExtract_Novel()
    {
        // Each iteration uses a unique host so every call is a novel registration.
        var uri = new Uri($"https://novel-{_counter++}.example.com/page");
        return await _extractor.ExtractAsync(_html, uri);
    }
}
