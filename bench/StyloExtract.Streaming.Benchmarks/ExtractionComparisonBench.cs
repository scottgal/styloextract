using System.IO.Hashing;
using System.Text;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.Streaming;

namespace StyloExtract.Streaming.Benchmarks;

[MemoryDiagnoser]
public class ExtractionComparisonBench
{
    [Params("home.html", "blog-sidecar.html", "blog-styloextract.html", "blog-fingerprint.html")]
    public string Fixture { get; set; } = "";

    private byte[] _htmlBytes = null!;
    private string _htmlString = null!;
    private StreamingPathSelector _selector = null!;
    private Guid _templateId;
    private ILayoutExtractor _extractor = null!;
    private Uri _uri = null!;
    private ServiceProvider _sp = null!;

    [GlobalSetup]
    public async Task Setup()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", Fixture);
        _htmlBytes = await File.ReadAllBytesAsync(path);
        _htmlString = Encoding.UTF8.GetString(_htmlBytes);

        // === Streaming path: hand-built template likely to match SOMETHING on a mostlylucid page ===
        // mostlylucid pages: <header>…</header><nav>…</nav>… <ul><li><a>…</a></li>… <p>…</p>
        // Fences picked to hit common structural runs across the fetched fixtures.
        var store = new InMemoryStreamingTemplateStore();
        _templateId = Guid.NewGuid();
        var template = new StreamingTemplate
        {
            TemplateId = _templateId,
            Host = "",
            PrefixFence = TemplateFence.BuildFromEvents(
                TagEvents("<header>", "</header>", "<nav>", "</nav>"),
                requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(
                TagEvents("<p>", "</p>", "<p>", "</p>"),
                requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(
                TagEvents("<footer>", "</footer>", "</body>", "</html>"),
                requiredDepth: 0),
            MinContentDepth = 0,
            BailoutBytes = 5_000_000,
            MaxCaptureBytes = 5_000_000,
            WindowSize = 4,
            MaxEventsWithoutTransition = 256,
        };
        await store.RegisterAsync(template);
        _selector = new StreamingPathSelector(store);

        // === Current path: full LayoutExtractor with warmed fast-path cache ===
        var services = new ServiceCollection();
        services.AddStyloExtract(o => o.StorePath = ":memory:");
        _sp = services.BuildServiceProvider();
        _extractor = _sp.GetRequiredService<ILayoutExtractor>();
        _uri = new Uri($"https://bench.example/{Fixture}");
        await _extractor.ExtractAsync(_htmlString, _uri); // register + warm fast-path
        await _extractor.ExtractAsync(_htmlString, _uri); // ensure cache is hot
    }

    [Benchmark(Baseline = true)]
    public async Task<ExtractionResult> Current_LayoutExtractor()
        => await _extractor.ExtractAsync(_htmlString, _uri);

    [Benchmark]
    public ScanVerdict New_StreamingScan()
        => _selector.Scan(_templateId, _htmlBytes);

    private static (ulong tagHash, ulong classHash)[] TagEvents(params string[] tags)
    {
        var result = new (ulong, ulong)[tags.Length];
        Span<byte> buf = stackalloc byte[64];
        for (int i = 0; i < tags.Length; i++)
        {
            var t = tags[i];
            var isClose = t.StartsWith("</", StringComparison.Ordinal);
            var nameStart = isClose ? 2 : 1;
            var nameEnd = t.IndexOf('>', nameStart);
            var name = t.AsSpan(nameStart, nameEnd - nameStart);
            var n = Encoding.UTF8.GetBytes(name, buf);
            result[i] = (XxHash3.HashToUInt64(buf[..n]), 0UL);
        }
        return result;
    }
}
