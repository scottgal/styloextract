using AngleSharp.Dom;
using BenchmarkDotNet.Attributes;
using StyloExtract.Abstractions;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;

namespace StyloExtract.Performance.Benchmarks;

/// <summary>
/// Times each stage of the LayoutExtractor pipeline in isolation. Each stage
/// runs against its prior stage's pre-computed input (parsed DOM cached in
/// <c>GlobalSetup</c>, cleaned DOM cached in setup, etc.) so the timing of
/// any one stage is not contaminated by the others. Reveals which stage to
/// tune next.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(warmupCount: 3, iterationCount: 5)]
public class PipelineStageBenchmarks
{
    private string _html = null!;

    private IHtmlDomParser _parser = null!;
    private IDomCleaner _cleaner = null!;
    private IStructuralFingerprinter _fingerprinter = null!;
    private IBlockSegmenter _segmenter = null!;
    private IBlockClassifier _classifier = null!;
    private IMarkdownRenderer _renderer = null!;

    // Pre-warmed inputs for each stage.
    private IDocument _parsedDoc = null!;       // for Stage_Clean
    private IDocument _cleanedDoc = null!;      // for Stage_Fingerprint, Stage_Segment
    private IReadOnlyList<IElement> _segments = null!; // for Stage_Classify
    private IReadOnlyList<ExtractedBlock> _blocks = null!; // for Stage_Render

    [Params("article-medium.html", "article-large.html", "table-heavy.html")]
    public string Fixture { get; set; } = "";

    [GlobalSetup]
    public void Setup()
    {
        _html = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture));

        _parser = new AngleSharpHtmlDomParser();
        _cleaner = new DomCleaner();
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        _fingerprinter = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        _segmenter = new BlockSegmenter();
        _classifier = HeuristicBlockClassifier.LoadFromEmbeddedResources();
        _renderer = new TypedMarkdownRenderer();

        _parsedDoc = _parser.Parse(_html);
        _cleanedDoc = _parser.Parse(_html);
        _cleaner.Clean(_cleanedDoc);
        _segments = _segmenter.Segment(_cleanedDoc);
        _blocks = _classifier.Classify(_segments);
    }

    [Benchmark]
    public IDocument Stage_Parse() => _parser.Parse(_html);

    [Benchmark]
    public IDocument Stage_Clean()
    {
        // Clean() mutates; reparse for each iteration so the second run sees
        // an unmodified document. Adds the parse cost, but the delta-vs-Parse
        // line in the summary table is the cleaner's true cost.
        var doc = _parser.Parse(_html);
        _cleaner.Clean(doc);
        return doc;
    }

    [Benchmark]
    public StructuralFingerprint Stage_Fingerprint() => _fingerprinter.Compute(_cleanedDoc);

    [Benchmark]
    public IReadOnlyList<IElement> Stage_Segment() => _segmenter.Segment(_cleanedDoc);

    [Benchmark]
    public IReadOnlyList<ExtractedBlock> Stage_Classify() => _classifier.Classify(_segments);

    [Benchmark]
    public string Stage_Render() => _renderer.Render(_blocks, ExtractionProfile.RagFull);
}
