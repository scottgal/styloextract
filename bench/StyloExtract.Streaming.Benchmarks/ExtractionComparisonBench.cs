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

    /// <summary>
    /// Host the bench registers the streaming template against. Matches a
    /// real mostlylucid.net page so the host-keyed hot-path is exercised
    /// the same way lucidview FULL hits it in production.
    /// </summary>
    private const string BenchHost = "www.mostlylucid.net";

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
        // alpha.18: register BOTH a GUID-keyed template (for the legacy Scan bench)
        // AND a host-keyed template under "www.mostlylucid.net" so the new
        // ScanByHost bench exercises the host hot-path that lucidview FULL uses.
        var store = new InMemoryStreamingTemplateStore();
        _templateId = Guid.NewGuid();
        var guidKeyedTemplate = new StreamingTemplate
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
            BailoutBytes = 5_000_000,
            MaxCaptureBytes = 5_000_000,
            WindowSize = 4,
            MaxEventsWithoutTransition = 256,
        };
        await store.RegisterAsync(guidKeyedTemplate);

        var hostKeyedTemplate = guidKeyedTemplate with
        {
            TemplateId = Guid.NewGuid(),
            Host = BenchHost,
        };
        await store.UpsertAsync(hostKeyedTemplate);

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

    /// <summary>
    /// alpha.18: benchmarks the host-keyed hot-path that
    /// <c>StreamingPathSelector.ScanByHost</c> exposes — the same code path
    /// lucidview FULL hits on every fetch. Should produce a verdict
    /// indistinguishable in wall-time from <see cref="New_StreamingScan"/> on
    /// the GUID-keyed path; if it diverges the host index has a cost worth
    /// surfacing.
    /// </summary>
    [Benchmark]
    public ScanVerdict New_StreamingScanByHost()
        => _selector.ScanByHost(BenchHost, _htmlBytes);

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
