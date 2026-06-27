using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class IncrementalFenceScannerTests
{
    [Fact]
    public async Task Whole_buffer_feed_matches_StreamingPathSelector_Scan()
    {
        var template = BuildTemplate();
        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(template);
        var selector = new StreamingPathSelector(store);

        var html = "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8.ToArray();
        var expected = selector.Scan(template.TemplateId, html);

        var scanner = IncrementalFenceScanner.Create(template);
        var actual = scanner.Feed(html);
        actual.Should().Be(expected);
    }

    [Fact]
    public void Per_byte_feed_yields_same_verdict_as_whole_buffer_feed()
    {
        var template = BuildTemplate();
        var html = "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8.ToArray();

        var whole = IncrementalFenceScanner.Create(template);
        var wholeVerdict = whole.Feed(html);
        whole.Flush(); // no-op once terminal

        var chunked = IncrementalFenceScanner.Create(template);
        ScanVerdict chunkedVerdict = ScanVerdict.Continue;
        for (int i = 0; i < html.Length; i++)
        {
            chunkedVerdict = chunked.Feed(html.AsSpan(i, 1));
            if (chunkedVerdict is ScanVerdict.Captured or ScanVerdict.Bailout) break;
        }
        chunkedVerdict = chunked.Flush();

        chunkedVerdict.Should().Be(wholeVerdict);
    }

    [Fact]
    public void Sixtyfour_byte_chunk_feed_yields_same_verdict()
    {
        var template = BuildTemplate();
        var html = BuildLongerPage();

        var whole = IncrementalFenceScanner.Create(template);
        whole.Feed(html);
        var wholeVerdict = whole.Flush();

        var chunked = IncrementalFenceScanner.Create(template);
        for (int i = 0; i < html.Length; i += 64)
        {
            var n = Math.Min(64, html.Length - i);
            var v = chunked.Feed(html.AsSpan(i, n));
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout) break;
        }
        var chunkedVerdict = chunked.Flush();

        chunkedVerdict.Should().Be(wholeVerdict);
    }

    [Fact]
    public void Feed_after_terminal_verdict_is_idempotent()
    {
        var template = BuildTemplate();
        var html = "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8.ToArray();

        var scanner = IncrementalFenceScanner.Create(template);
        var v1 = scanner.Feed(html);
        v1.Should().Be(ScanVerdict.Captured);

        // Subsequent feed should not crash and should report the latched terminal verdict.
        var v2 = scanner.Feed("<extra>garbage</extra>"u8);
        v2.Should().Be(ScanVerdict.Captured);
    }

    private static StreamingTemplate BuildTemplate() =>
        TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagClaim("header"),
            TripwireTestHelpers.TagClaim("article"),
            TripwireTestHelpers.TagClaim("article"),
            bailoutBytes: 100_000,
            maxCaptureBytes: 100_000)
        with { Host = "test.local" };

    private static byte[] BuildLongerPage()
    {
        var sb = new StringBuilder();
        sb.Append("<html><body>");
        sb.Append("<header>");
        for (int i = 0; i < 10; i++) sb.Append("<nav>menu</nav>");
        sb.Append("</header>");
        sb.Append("<article>");
        for (int i = 0; i < 30; i++) sb.Append("<p>paragraph text here</p>");
        sb.Append("</article>");
        sb.Append("<footer>copyright</footer>");
        sb.Append("</body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
