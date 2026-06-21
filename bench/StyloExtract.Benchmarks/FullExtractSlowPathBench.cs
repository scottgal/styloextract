using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;

namespace StyloExtract.Benchmarks;

/// <summary>
/// Measures ExtractAsync with a slightly perturbed version of an already-registered page.
/// The perturbation changes enough structure to miss the fast-path LSH but still land in
/// the cosine-based slow path.
/// Spec §13 target: SlowPath &lt;= 30ms p99.
/// </summary>
[MemoryDiagnoser]
public class FullExtractSlowPathBench
{
    private ILayoutExtractor _extractor = null!;
    private string _htmlBase = null!;
    private string _htmlPerturbed = null!;
    private Uri _uri = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        var sp = services.BuildServiceProvider();
        _extractor = sp.GetRequiredService<ILayoutExtractor>();
        _htmlBase = File.ReadAllText("article.html");
        _uri = new Uri("https://bench.slowpath.com/page");

        // Register the base page as a novel template.
        await _extractor.ExtractAsync(_htmlBase, _uri);

        // Perturb the page: add a new paragraph section that alters shingles enough
        // to miss LSH bands but keeps enough structural similarity for cosine match.
        _htmlPerturbed = _htmlBase.Replace(
            "<footer>",
            "<section class=\"extra-content\"><p>" +
            string.Concat(Enumerable.Repeat("extra perturbed content for slow path testing ", 50)) +
            "</p></section>\n<footer>");
    }

    [Benchmark]
    public async Task<ExtractionResult> FullExtract_SlowPath()
        => await _extractor.ExtractAsync(_htmlPerturbed, _uri);
}
