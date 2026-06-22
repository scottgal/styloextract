using System.Reflection;
using AngleSharp.Dom;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;

namespace StyloExtract.Benchmarks;

/// <summary>
/// Input size selector for per-component benchmarks.
///   Small  ~5 KB   single article, minimal chrome
///   Medium ~50 KB  blog article with comments, sidebar, related-posts grid
///   Large  ~200 KB news article with Gutenberg blocks, extensive nav, tables, 100+ paragraphs
/// </summary>
public enum SourceSize { Small, Medium, Large }

// ---------------------------------------------------------------------------
// Shared infrastructure: HTML loading, DI container, reflection helpers
// ---------------------------------------------------------------------------

internal static class BenchShared
{
    private static readonly Assembly BenchAsm = typeof(BenchShared).Assembly;
    private static IServiceProvider? _sp;
    private static readonly object Lock = new();

    public static string LoadHtml(SourceSize size)
    {
        var name = size switch
        {
            SourceSize.Small => "small",
            SourceSize.Medium => "medium",
            SourceSize.Large => "large",
            _ => throw new ArgumentOutOfRangeException(nameof(size))
        };
        var resName = BenchAsm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith($"{name}.html", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException(
                $"Embedded resource '{name}.html' not found. " +
                "Ensure it is listed as <EmbeddedResource> in the .csproj.");
        using var stream = BenchAsm.GetManifestResourceStream(resName)!;
        using var reader = new System.IO.StreamReader(stream);
        return reader.ReadToEnd();
    }

    public static IServiceProvider GetServiceProvider()
    {
        if (_sp is not null) return _sp;
        lock (Lock)
        {
            if (_sp is not null) return _sp;
            var svc = new ServiceCollection();
            svc.AddStyloExtract(o => o.StorePath = ":memory:");
            _sp = svc.BuildServiceProvider();
            return _sp;
        }
    }

    // Access to internal RepeatedItemDetector.Detect via reflection (cannot modify prod code).
    private static readonly MethodInfo? RepeatedItemDetectMethod = LoadRepeatedItemDetect();
    private static MethodInfo? LoadRepeatedItemDetect()
    {
        var type = typeof(HeuristicBlockClassifier).Assembly
            .GetType("StyloExtract.Heuristics.RepeatedItemDetector");
        return type?.GetMethod("Detect", BindingFlags.Public | BindingFlags.Static);
    }

    public static int DetectRepeatedItems(IElement root)
    {
        if (RepeatedItemDetectMethod is null) return 0;
        var result = RepeatedItemDetectMethod.Invoke(null, [root]);
        return result is System.Collections.ICollection col ? col.Count : 0;
    }

    // Access to internal IntraBlockCleaner.Clean via reflection.
    private static readonly MethodInfo? IntraBlockCleanMethod = LoadIntraBlockClean();
    private static MethodInfo? LoadIntraBlockClean()
    {
        var type = typeof(HeuristicBlockClassifier).Assembly
            .GetType("StyloExtract.Heuristics.IntraBlockCleaner");
        return type?.GetMethod("Clean", BindingFlags.Public | BindingFlags.Static);
    }

    public static void IntraBlockClean(IElement element)
        => IntraBlockCleanMethod?.Invoke(null, [element]);

    public static LearnedExtractor BuildExtractor(
        (BlockRole Role, string[] Selectors)[] rules)
    {
        var blockRules = rules.Select((r, i) => new BlockRule
        {
            RuleId = $"rule-{i:D3}",
            Role = r.Role,
            CssSelectors = r.Selectors,
            MeanConfidence = 0.85,
            ObservationCount = 50,
            DriftScore = 0.02
        }).ToList();

        return new LearnedExtractor
        {
            TemplateId = Guid.NewGuid(),
            Version = 1,
            Rules = blockRules,
            Centroid = new ExtractorCentroidState
            {
                TotalObservations = 50,
                ByRole = new Dictionary<BlockRole, RoleCentroid>(),
                OverallDriftScore = 0.02,
                LastObservation = DateTimeOffset.UtcNow
            }
        };
    }
}

// ---------------------------------------------------------------------------
// 1. ParseBench — IHtmlDomParser.Parse (AngleSharp parse + tree build)
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class ParseBench
{
    private IHtmlDomParser _parser = null!;
    private string _html = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _parser = new AngleSharpHtmlDomParser();
        _html = BenchShared.LoadHtml(Size);
    }

    [Benchmark]
    public IDocument Parse() => _parser.Parse(_html, new Uri("https://bench.example.com/page"));
}

// ---------------------------------------------------------------------------
// 2. DomCleanerBench — IDomCleaner.Clean
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class DomCleanerBench
{
    private IHtmlDomParser _parser = null!;
    private IDomCleaner _cleaner = null!;
    private string _html = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _parser = new AngleSharpHtmlDomParser();
        _cleaner = new DomCleaner();
        _html = BenchShared.LoadHtml(Size);
    }

    [Benchmark]
    public void Clean()
    {
        var doc = _parser.Parse(_html);
        _cleaner.Clean(doc);
    }
}

// ---------------------------------------------------------------------------
// 3. SegmenterBench — IBlockSegmenter.Segment
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class SegmenterBench
{
    private IBlockSegmenter _segmenter = null!;
    private IDocument _doc = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _segmenter = sp.GetRequiredService<IBlockSegmenter>();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        var cleaner = sp.GetRequiredService<IDomCleaner>();
        _doc = parser.Parse(BenchShared.LoadHtml(Size));
        cleaner.Clean(_doc);
    }

    [Benchmark]
    public IReadOnlyList<IElement> Segment() => _segmenter.Segment(_doc);
}

// ---------------------------------------------------------------------------
// 4. FingerprintBench — IStructuralFingerprinter.Compute
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class FingerprintBench
{
    private IStructuralFingerprinter _fingerprinter = null!;
    private IDocument _doc = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _fingerprinter = sp.GetRequiredService<IStructuralFingerprinter>();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        var cleaner = sp.GetRequiredService<IDomCleaner>();
        _doc = parser.Parse(BenchShared.LoadHtml(Size));
        cleaner.Clean(_doc);
    }

    [Benchmark]
    public StructuralFingerprint Fingerprint() => _fingerprinter.Compute(_doc);
}

// ---------------------------------------------------------------------------
// 5. ClassifyBench — IBlockClassifier.Classify (full v1.5.x pass)
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class ClassifyBench
{
    private IBlockClassifier _classifier = null!;
    private IHtmlDomParser _parser = null!;
    private IDomCleaner _cleaner = null!;
    private IBlockSegmenter _segmenter = null!;
    private string _html = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _classifier = sp.GetRequiredService<IBlockClassifier>();
        _parser = sp.GetRequiredService<IHtmlDomParser>();
        _cleaner = sp.GetRequiredService<IDomCleaner>();
        _segmenter = sp.GetRequiredService<IBlockSegmenter>();
        _html = BenchShared.LoadHtml(Size);
    }

    [Benchmark]
    public IReadOnlyList<ExtractedBlock> Classify()
    {
        // Re-parse each iteration: IntraBlockCleaner mutates the DOM so we need a fresh document.
        var doc = _parser.Parse(_html);
        _cleaner.Clean(doc);
        var elements = _segmenter.Segment(doc);
        return _classifier.Classify(elements);
    }
}

// ---------------------------------------------------------------------------
// 6. IntraBlockCleanerBench — IntraBlockCleaner.Clean on a content element
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class IntraBlockCleanerBench
{
    private IHtmlDomParser _parser = null!;
    private IDomCleaner _cleaner = null!;
    private string _html = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _parser = sp.GetRequiredService<IHtmlDomParser>();
        _cleaner = sp.GetRequiredService<IDomCleaner>();
        _html = BenchShared.LoadHtml(Size);
    }

    [Benchmark]
    public void IntraBlockClean()
    {
        var doc = _parser.Parse(_html);
        _cleaner.Clean(doc);
        var target = (IElement?)(
            doc.QuerySelector("article.post-single") ??
            doc.QuerySelector("article.wp-block-post-content") ??
            doc.QuerySelector("article") ??
            doc.QuerySelector("main") ??
            doc.QuerySelector("[class*='entry-content']"));
        if (target is not null)
            BenchShared.IntraBlockClean(target);
    }
}

// ---------------------------------------------------------------------------
// 7. RepeatedItemDetectorBench — RepeatedItemDetector.Detect
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class RepeatedItemDetectorBench
{
    private IElement _body = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        var cleaner = sp.GetRequiredService<IDomCleaner>();
        var doc = parser.Parse(BenchShared.LoadHtml(Size));
        cleaner.Clean(doc);
        _body = doc.Body!;
    }

    [Benchmark]
    public int Detect() => BenchShared.DetectRepeatedItems(_body);
}

// ---------------------------------------------------------------------------
// 8a. JsonLdFallbackNoSchemaBench — no JSON-LD scripts present
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class JsonLdFallbackNoSchemaBench
{
    private IDocument _doc = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        _doc = parser.Parse(BenchShared.LoadHtml(Size));
        // Strip any ld+json scripts to test the early-exit path.
        foreach (var el in _doc.QuerySelectorAll("script[type='application/ld+json']").ToArray())
            el.Remove();
    }

    [Benchmark]
    public string? Extract() => JsonLdContentExtractor.ExtractMainContent(_doc);
}

// ---------------------------------------------------------------------------
// 8b. JsonLdFallbackWithSchemaBench — JSON-LD scripts present
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class JsonLdFallbackWithSchemaBench
{
    private IDocument _docSmall = null!;
    private IDocument _docMedium = null!;
    private IDocument _docLarge = null!;

    private const string SmallSchemaLd = """
        <script type="application/ld+json">
        {
          "@context": "https://schema.org",
          "@type": "Article",
          "headline": "Understanding Dependency Injection in ASP.NET Core",
          "author": {"@type": "Person", "name": "Alice Thompson"},
          "datePublished": "2026-06-15",
          "description": "A concise guide to dependency injection.",
          "articleBody": "Dependency injection is a foundational pattern in modern .NET application development. When you register services in the container, the framework automatically resolves and injects them wherever they are needed. This decoupling makes components easier to test, maintain, and replace without touching the call sites that depend on them. The built-in DI container in ASP.NET Core supports three service lifetimes: singleton, scoped, and transient. Choosing the correct lifetime prevents subtle bugs."
        }
        </script>
        """;

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        var parser = sp.GetRequiredService<IHtmlDomParser>();

        var smallWithSchema = BenchShared.LoadHtml(SourceSize.Small)
            .Replace("</head>", SmallSchemaLd + "</head>", StringComparison.Ordinal);
        _docSmall = parser.Parse(smallWithSchema);
        _docMedium = parser.Parse(BenchShared.LoadHtml(SourceSize.Medium));
        _docLarge = parser.Parse(BenchShared.LoadHtml(SourceSize.Large));
    }

    [Benchmark]
    public string? ExtractSmall() => JsonLdContentExtractor.ExtractMainContent(_docSmall);

    [Benchmark]
    public string? ExtractMedium() => JsonLdContentExtractor.ExtractMainContent(_docMedium);

    [Benchmark]
    public string? ExtractLarge() => JsonLdContentExtractor.ExtractMainContent(_docLarge);
}

// ---------------------------------------------------------------------------
// 9. MarkdownRenderBench — IMarkdownRenderer.Render across three profiles
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class MarkdownRenderBench
{
    private IMarkdownRenderer _renderer = null!;
    private IReadOnlyList<ExtractedBlock> _blocksSmall = null!;
    private IReadOnlyList<ExtractedBlock> _blocksMedium = null!;
    private IReadOnlyList<ExtractedBlock> _blocksLarge = null!;

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _renderer = sp.GetRequiredService<IMarkdownRenderer>();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        var cleaner = sp.GetRequiredService<IDomCleaner>();
        var segmenter = sp.GetRequiredService<IBlockSegmenter>();
        var classifier = sp.GetRequiredService<IBlockClassifier>();

        _blocksSmall = ClassifyFresh(SourceSize.Small, parser, cleaner, segmenter, classifier);
        _blocksMedium = ClassifyFresh(SourceSize.Medium, parser, cleaner, segmenter, classifier);
        _blocksLarge = ClassifyFresh(SourceSize.Large, parser, cleaner, segmenter, classifier);
    }

    [Benchmark] public string RenderSmall_MainContent() => _renderer.Render(_blocksSmall, ExtractionProfile.MainContentOnly);
    [Benchmark] public string RenderSmall_RagFull() => _renderer.Render(_blocksSmall, ExtractionProfile.RagFull);
    [Benchmark] public string RenderSmall_AgentNav() => _renderer.Render(_blocksSmall, ExtractionProfile.AgentNavigation);

    [Benchmark] public string RenderMedium_MainContent() => _renderer.Render(_blocksMedium, ExtractionProfile.MainContentOnly);
    [Benchmark] public string RenderMedium_RagFull() => _renderer.Render(_blocksMedium, ExtractionProfile.RagFull);
    [Benchmark] public string RenderMedium_AgentNav() => _renderer.Render(_blocksMedium, ExtractionProfile.AgentNavigation);

    [Benchmark] public string RenderLarge_MainContent() => _renderer.Render(_blocksLarge, ExtractionProfile.MainContentOnly);
    [Benchmark] public string RenderLarge_RagFull() => _renderer.Render(_blocksLarge, ExtractionProfile.RagFull);
    [Benchmark] public string RenderLarge_AgentNav() => _renderer.Render(_blocksLarge, ExtractionProfile.AgentNavigation);

    private static IReadOnlyList<ExtractedBlock> ClassifyFresh(
        SourceSize size,
        IHtmlDomParser parser,
        IDomCleaner cleaner,
        IBlockSegmenter segmenter,
        IBlockClassifier classifier)
    {
        var doc = parser.Parse(BenchShared.LoadHtml(size));
        cleaner.Clean(doc);
        var elements = segmenter.Segment(doc);
        return classifier.Classify(elements);
    }
}

// ---------------------------------------------------------------------------
// 10. ApplicatorBench — IExtractorApplicator.Apply with learned extractor rules
// ---------------------------------------------------------------------------

[MemoryDiagnoser]
public class ApplicatorBench
{
    private IExtractorApplicator _applicator = null!;
    private IDocument _doc = null!;
    private LearnedExtractor _extractor = null!;

    [ParamsAllValues]
    public SourceSize Size { get; set; }

    private static readonly (BlockRole Role, string[] Selectors)[] SmallRules =
    [
        (BlockRole.Header, ["header.site-header"]),
        (BlockRole.PrimaryNavigation, ["nav.main-nav"]),
        (BlockRole.MainContent, ["article.post-single", ".entry-content", "article"]),
        (BlockRole.Footer, ["footer.site-footer"]),
    ];

    private static readonly (BlockRole Role, string[] Selectors)[] MediumRules =
    [
        (BlockRole.Header, ["header.site-header"]),
        (BlockRole.PrimaryNavigation, ["nav.main-navigation", "nav.primary-nav"]),
        (BlockRole.Breadcrumb, ["nav.breadcrumb-nav", ".breadcrumb"]),
        (BlockRole.MainContent, ["article.post", ".entry-content", ".wp-block-post-content", "article"]),
        (BlockRole.Sidebar, ["aside.sidebar"]),
        (BlockRole.Footer, ["footer.site-footer"]),
        (BlockRole.CookieBanner, [".cookie-banner", "#cookie-banner"]),
    ];

    private static readonly (BlockRole Role, string[] Selectors)[] LargeRules =
    [
        (BlockRole.Header, ["header.site-header"]),
        (BlockRole.PrimaryNavigation, ["nav.primary-nav", ".primary-nav"]),
        (BlockRole.Breadcrumb, ["nav.breadcrumb", ".breadcrumb"]),
        (BlockRole.MainContent, ["article.post-single", ".entry-content", ".wp-block-post-content", "article"]),
        (BlockRole.Sidebar, ["aside.sidebar"]),
        (BlockRole.Footer, ["footer.site-footer"]),
        (BlockRole.CookieBanner, [".cookie-consent-banner", "#cookie-consent"]),
        (BlockRole.Table, ["table.wp-block-table"]),
    ];

    [GlobalSetup]
    public void Setup()
    {
        var sp = BenchShared.GetServiceProvider();
        _applicator = sp.GetRequiredService<IExtractorApplicator>();
        var parser = sp.GetRequiredService<IHtmlDomParser>();
        var cleaner = sp.GetRequiredService<IDomCleaner>();

        var doc = parser.Parse(BenchShared.LoadHtml(Size));
        cleaner.Clean(doc);
        _doc = doc;

        _extractor = Size switch
        {
            SourceSize.Small => BenchShared.BuildExtractor(SmallRules),
            SourceSize.Medium => BenchShared.BuildExtractor(MediumRules),
            SourceSize.Large => BenchShared.BuildExtractor(LargeRules),
            _ => throw new ArgumentOutOfRangeException(nameof(Size))
        };
    }

    [Benchmark]
    public ApplicatorResult Apply() => _applicator.Apply(_doc, _extractor);
}
