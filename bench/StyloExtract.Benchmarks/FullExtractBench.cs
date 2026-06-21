using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class FullExtractBench
{
    private ILayoutExtractor _extractor = null!;
    private string _html = null!;
    private Uri _uri = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        _extractor = sp.GetRequiredService<ILayoutExtractor>();
        _html = File.ReadAllText("article.html");
        _uri = new Uri("https://bench.example.com/page");
        // Warm: first call registers; second is fast-path
        await _extractor.ExtractAsync(_html, _uri);
    }

    [Benchmark]
    public async Task<ExtractionResult> FullExtract_CacheHit() => await _extractor.ExtractAsync(_html, _uri);
}
