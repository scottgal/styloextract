using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Task 4 (alpha.24) tripwire-shape variant of the alpha.23 regression net.
/// Verifies the inducer ↔ scanner agreement contract: induce a template from
/// some bytes, then scan those same bytes through the scanner — the verdict
/// must be Captured (modulo Bailout fallback if the inducer picked a target
/// that the scanner can't reach).
/// </summary>
public sealed class InducerScannerAgreementTests
{
    private readonly ITestOutputHelper _out;
    public InducerScannerAgreementTests(ITestOutputHelper o) { _out = o; }

    [Fact]
    public async Task Induced_template_scans_realistic_html_to_Captured()
    {
        var html = Encoding.UTF8.GetBytes(
            "<!DOCTYPE html><html><head>" +
            "<meta charset=\"utf-8\"><title>x</title>" +
            "<script>var x=1;</script>" +
            "<link rel=\"stylesheet\" href=\"a.css\">" +
            "</head><body>" +
            "<div class=\"banner\"><img src=\"x.png\"><p>cookie banner</p></div>" +
            "<header><nav><ul><li><a>home</a></li><li><a>about</a></li></ul></nav></header>" +
            "<main><article>" +
            "<h1>Title</h1>" +
            "<p>First paragraph <a href=\"x\">link</a> with <img src=\"y.png\"> images.</p>" +
            "<p>Second paragraph also non-trivial.</p>" +
            "<p>Third paragraph for good measure.</p>" +
            "<p>Fourth paragraph closes the cluster.</p>" +
            "</article></main>" +
            "<footer><p>(c) 2026</p></footer>" +
            "</body></html>");

        var inducer = new StreamingTemplateInducer();
        var template = inducer.Induce("agreement.example", html);
        template.Should().NotBeNull();

        var store = new InMemoryStreamingTemplateStore();
        await store.UpsertAsync(template!);

        // Drive through IncrementalFenceScanner with chunked feed so we can
        // surface PeakBufferedBytes for the integration metric.
        var scanner = IncrementalFenceScanner.Create(template!);
        const int chunkSize = 256;
        ScanVerdict verdict = ScanVerdict.Continue;
        for (int i = 0; i < html.Length; i += chunkSize)
        {
            var n = Math.Min(chunkSize, html.Length - i);
            verdict = scanner.Feed(html.AsSpan(i, n));
            if (verdict is ScanVerdict.Captured or ScanVerdict.Bailout) break;
        }
        if (verdict == ScanVerdict.Continue) verdict = scanner.Flush();

        _out.WriteLine($"[integration] verdict={verdict} peakBuffered={scanner.PeakBufferedBytes}B " +
                       $"bytesConsumed={scanner.BytesConsumed}B captureRange=[{scanner.CaptureStartByte},{scanner.CaptureEndByte})");

        verdict.Should().Be(ScanVerdict.Captured,
            "tripwire matching is exact — induced claims must fire on the same bytes the inducer saw");
    }

    [Fact]
    public async Task Induced_template_scans_real_mostlylucid_home_to_Captured_or_Bailout()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "mostlylucid-home.html");
        File.Exists(path).Should().BeTrue("mostlylucid fixture should be copied to output");
        var html = File.ReadAllBytes(path);

        var inducer = new StreamingTemplateInducer();
        var template = inducer.Induce("www.mostlylucid.net", html);
        template.Should().NotBeNull();

        var store = new InMemoryStreamingTemplateStore();
        await store.UpsertAsync(template!);

        var selector = new StreamingPathSelector(store);
        var verdict = selector.ScanByHost("www.mostlylucid.net", html);

        // Either Captured or Bailout proves the scanner exercised the host-keyed
        // induced template. NoTemplate or Continue would indicate the pipeline
        // didn't actually run — that's the regression we're guarding against.
        verdict.Should().NotBe(ScanVerdict.NoTemplate);
        verdict.Should().NotBe(ScanVerdict.Continue);
    }

    [Fact]
    public void Scanner_with_no_tripwire_match_flushes_to_Bailout()
    {
        ReadOnlySpan<byte> html =
            "<html><body><div><p>only divs and paragraphs here, no header/footer/article</p>"u8;
        var html2 = "</div></body></html>"u8;

        var unmatchable = TripwireTestHelpers.TagClaim("definitely-not-an-html-tag");
        var template = TripwireTestHelpers.MakeTemplate(
            unmatchable, unmatchable, unmatchable,
            bailoutBytes: 100_000, maxCaptureBytes: 100_000)
            with { Host = "bailout.example" };

        var scanner = IncrementalFenceScanner.Create(template);
        scanner.Feed(html);
        scanner.Feed(html2);
        var vFlush = scanner.Flush();

        vFlush.Should().Be(ScanVerdict.Bailout,
            "Flush() at end-of-stream must latch Continue→Bailout — Continue at EOF is meaningless");
    }
}
