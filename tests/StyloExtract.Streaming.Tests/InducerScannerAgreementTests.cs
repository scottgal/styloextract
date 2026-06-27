using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// alpha.23 regression net for the inducer↔scanner agreement contract that
/// silently broke between alpha.21 and alpha.22:
///
/// <para>
/// <b>Depth tracking on real HTML.</b> alpha.21 added a depth-aware
/// ContentEnd check (<c>s.Depth &lt;= s.DepthAtCaptureStart</c>) but the
/// depth counter incremented on EVERY emitted tag — including void elements
/// (img/br/input/meta/link/hr) and inline nodes (a/span/i/b/em/…), none of
/// which have a corresponding close in real HTML. On mostlylucid.net the
/// scanner reached <c>Depth=206</c> at <c>&lt;/body&gt;</c> versus
/// <c>DepthAtCaptureStart=75</c> — the gate never opened, so the
/// <c>ContentEndFence</c> never fired even though its MinHash signature was
/// byte-identical to the scanner's window sketch. Restricting depth tracking
/// to structural tags only (matching the sketch filter introduced in alpha.21)
/// makes depth honest and the gate functional.
/// </para>
///
/// <para>
/// <b>End-of-stream verdict.</b> Before alpha.23 the scanner returned
/// <c>Continue</c> when a byte stream exhausted without matching all fences.
/// <c>Continue</c> at EOF is meaningless to a consumer that has nothing more
/// to feed — <see cref="IncrementalFenceScanner.Flush"/> now latches the
/// terminal verdict to <see cref="ScanVerdict.Bailout"/> so callers can
/// fall through to the slow path / re-induction cleanly.
/// </para>
/// </summary>
public sealed class InducerScannerAgreementTests
{
    [Fact]
    public async Task Induced_template_scans_realistic_html_to_Captured()
    {
        // Realistic HTML with chrome BEFORE the header and a mix of void
        // elements (img/meta/link) + inline tags (a/span). Pre-alpha.23
        // these inflate the scanner's depth counter unboundedly and the
        // depth-aware ContentEnd check never fires.
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

        var selector = new StreamingPathSelector(store);
        var verdict = selector.ScanByHost("agreement.example", html);

        verdict.Should().Be(ScanVerdict.Captured,
            "the inducer's fences must align with the scanner's sliding-window sketch on the SAME bytes — " +
            "regardless of how many void or inline tags appear in the stream");
    }

    [Fact]
    public async Task Induced_template_scans_real_mostlylucid_home_to_Captured()
    {
        // The exact byte stream that caught alpha.21/alpha.22 in dogfood smoke.
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

        verdict.Should().Be(ScanVerdict.Captured,
            "induce-then-scan on the SAME bytes must reach Captured — Continue means the scanner never matched any fence");
    }

    [Fact]
    public void Scanner_with_no_fence_match_flushes_to_Bailout()
    {
        // Build a template whose fences will NEVER match the input stream.
        // Feed it bytes, Flush(), and confirm the verdict latches to Bailout
        // rather than dangling at Continue at EOF.
        ReadOnlySpan<byte> html =
            "<html><body><div><p>only divs and paragraphs here, no header/footer/article</p>"u8;
        var html2 = "</div></body></html>"u8;

        // Unmatchable fences — synthetic tag hashes that aren't in the input.
        var unmatchable = TemplateFence.BuildFromEvents(
            new (ulong, ulong)[]
            {
                (0xDEADBEEFUL, 0UL),
                (0xCAFEBABEUL, 0UL),
                (0xFEEDFACEUL, 0UL),
                (0xBAADF00DUL, 0UL),
            },
            requiredDepth: 0);

        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "bailout.example",
            PrefixFence = unmatchable,
            ContentStartFence = unmatchable,
            ContentEndFence = unmatchable,
            BailoutBytes = 100_000,
            MaxCaptureBytes = 100_000,
            WindowSize = 4,
            MaxEventsWithoutTransition = 256,
        };

        var scanner = IncrementalFenceScanner.Create(template);
        scanner.Feed(html);
        scanner.Feed(html2);
        var vFlush = scanner.Flush();

        vFlush.Should().Be(ScanVerdict.Bailout,
            "Flush() at end-of-stream must latch Continue→Bailout — Continue at EOF is meaningless");
    }
}
