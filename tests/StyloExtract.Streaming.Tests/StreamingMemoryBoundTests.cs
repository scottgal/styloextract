using System.Text;
using FluentAssertions;
using Xunit;
using Xunit.Abstractions;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Pins the bounded-memory contract: regardless of how many bytes are fed
/// through the byte-pattern scanner, the carry-over buffer stays bounded by
/// the longest carry needed for pattern stitching, NEVER O(response).
///
/// Regression guard against re-introducing growable-up-to-1MiB buffer
/// behaviour.
/// </summary>
public sealed class StreamingMemoryBoundTests
{
    private readonly ITestOutputHelper _out;
    public StreamingMemoryBoundTests(ITestOutputHelper o) { _out = o; }

    private const int ChunkSize = 4 * 1024;

    [Fact]
    public void IncrementalTokenizer_FivePlusMiB_StaysUnderBufferCap()
    {
        var html = GenerateLargeHtml(megabytes: 5);
        var tok = new IncrementalHtmlTokenizer();

        int offset = 0;
        int events = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(ChunkSize, html.Length - offset);
            tok.Feed(html.AsSpan(offset, take));
            while (tok.TryReadTag(out _)) events++;
            offset += take;
        }
        while (tok.TryReadTag(out _)) events++;

        events.Should().BeGreaterThan(0, "the synthetic page is tag-heavy");
        tok.BytesConsumed.Should().BeGreaterThan(4 * 1024 * 1024,
            "we fed multi-MiB; consumed should reflect that");
        tok.PeakBufferedBytes.Should().BeLessThan(tok.MaxPartialTagBytes,
            $"sliding-window contract violated: peak buffered = {tok.PeakBufferedBytes:N0} B " +
            $"after consuming {tok.BytesConsumed:N0} B");
        // Tighter assertion: the synthetic page's longest single tag is
        // well under 1 KiB. Peak should sit there regardless of how high
        // MaxPartialTagBytes is configured.
        tok.PeakBufferedBytes.Should().BeLessThan(2 * 1024,
            "synthetic page has no >1 KiB tags; peak should reflect that, not the configured ceiling");
    }

    [Fact]
    public void IncrementalTokenizer_HoldsNoBytesBetweenDrainedFeeds()
    {
        var html = GenerateLargeHtml(megabytes: 1);
        var tok = new IncrementalHtmlTokenizer();

        int offset = 0;
        int maxResidual = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(ChunkSize, html.Length - offset);
            tok.Feed(html.AsSpan(offset, take));
            while (tok.TryReadTag(out _)) { }
            var consumedSoFar = tok.BytesConsumed;
            var fedSoFar = offset + take;
            var residual = (int)(fedSoFar - consumedSoFar);
            if (residual > maxResidual) maxResidual = residual;
            offset += take;
        }

        maxResidual.Should().BeLessThan(tok.MaxPartialTagBytes,
            $"residual between fed and consumed should bound to O(longest tag); got {maxResidual:N0} B");
        maxResidual.Should().BeLessThan(2 * 1024,
            "synthetic page tags are well under 1 KiB; residual should reflect that");
    }

    [Fact]
    public void IncrementalScanner_FivePlusMiB_HoldsBoundedBytes()
    {
        // Drive a multi-MiB response through the incremental byte-pattern
        // scanner. Verdict isn't what's measured here — peak buffered bytes is.
        var template = BuildTrivialTemplate();
        var scanner = IncrementalBytePatternScanner.Create(template);

        var html = GenerateLargeHtml(megabytes: 5);
        int offset = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(ChunkSize, html.Length - offset);
            var v = scanner.Feed(html.AsSpan(offset, take));
            offset += take;
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout) continue;
        }
        scanner.Flush();

        scanner.BytesConsumed.Should().BeGreaterThan(0);
        scanner.PeakBufferedBytes.Should().BeLessThan(scanner.MaxCarryBufferBytes,
            $"scanner held {scanner.PeakBufferedBytes:N0} B vs response {html.Length:N0} B; bounded-memory broken");
        scanner.PeakBufferedBytes.Should().BeLessThan(8 * 1024,
            "trivial-template carry should be small regardless of configured ceiling");
    }

    [Fact]
    public void BytePatternScanner_200KB_16KB_chunks_PeakBytesProbe()
    {
        // Headline measurement for Task 13: peak buffered bytes on the
        // existing 200 KB / 16 KB chunk integration setup. Logs to xUnit
        // output so the implementer can read the actual number off the
        // test report.
        var template = BuildTrivialTemplate();
        var html = GenerateLargeHtml(megabytes: 1).AsSpan(0, 200_000).ToArray();
        var scanner = IncrementalBytePatternScanner.Create(template);
        const int chunkSize = 16 * 1024;
        ScanVerdict v = ScanVerdict.Continue;
        int offset = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(chunkSize, html.Length - offset);
            v = scanner.Feed(html.AsSpan(offset, take));
            offset += take;
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout) break;
        }
        if (v == ScanVerdict.Continue) v = scanner.Flush();

        _out.WriteLine($"[Task13 probe] html=200_000B chunks=16KB verdict={v} " +
                       $"peakBuffered={scanner.PeakBufferedBytes}B " +
                       $"bytesConsumed={scanner.BytesConsumed}B " +
                       $"captureRange=[{scanner.CaptureStartByte},{scanner.CaptureEndByte})");

        scanner.PeakBufferedBytes.Should().BeLessThan(scanner.MaxCarryBufferBytes);
        scanner.PeakBufferedBytes.Should().BeLessThan(8 * 1024,
            "trivial-template carry should be small regardless of configured ceiling");
    }

    private static byte[] GenerateLargeHtml(int megabytes)
    {
        var target = megabytes * 1024 * 1024;
        var sb = new StringBuilder(target + 1024);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        sb.Append("<title>Synthetic</title></head><body>");
        sb.Append("<header><nav><ul>");
        for (int i = 0; i < 10; i++) sb.Append("<li><a href=\"/x\">nav</a></li>");
        sb.Append("</ul></nav></header>");
        sb.Append("<main><article>");
        int para = 0;
        while (sb.Length < target)
        {
            sb.Append("<section class=\"s")
                .Append(para % 8)
                .Append("\"><h2>Heading ")
                .Append(para)
                .Append("</h2><p class=\"lead\">Lorem ipsum dolor sit amet, consectetur ")
                .Append("adipiscing elit. Sed do eiusmod tempor incididunt.</p>")
                .Append("<ul><li>alpha</li><li>beta</li><li>gamma</li></ul>")
                .Append("</section>");
            para++;
            if ((para & 0x3F) == 0)
            {
                sb.Append("<script>var x = ")
                    .Append(para)
                    .Append(";</script><!-- marker ")
                    .Append(para)
                    .Append(" -->");
            }
        }
        sb.Append("</article></main><footer>copyright</footer></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static StreamingTemplate BuildTrivialTemplate()
    {
        return TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"),
            bailoutBytes: 10_000_000,
            maxCaptureBytes: 10_000_000)
            with { Host = "synthetic.test" };
    }
}
