using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

[MemoryDiagnoser]
public class AllocationBench
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
        await _extractor.ExtractAsync(_html, _uri); // warm
    }

    [Benchmark]
    public async Task<long> CacheHit_Allocations()
    {
        var result = await _extractor.ExtractAsync(_html, _uri);
        return result.Stats.BlockCount;
    }
}
