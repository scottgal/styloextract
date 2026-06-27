using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

/// <summary>
/// Pins the alpha.19 sliding-window memory contract: regardless of how many
/// bytes are fed through the tokenizer / scanner, the in-flight buffer stays
/// bounded by the longest tag observed (plus chunk slack), NEVER O(response).
///
/// These tests are the regression guard against re-introducing alpha.18's
/// growable-up-to-1MiB buffer behaviour.
/// </summary>
public sealed class StreamingMemoryBoundTests
{
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

        // alpha.21 contract: peak in-flight is O(longest partial tag at a
        // chunk boundary) — NOT chunk-size. The longest tag in the fixture
        // is well under 256 B; realistic peak is low hundreds of bytes,
        // often zero when boundaries land in text. 4 KiB is the MaxBufferSize
        // cap — peak must stay strictly below it.
        tok.PeakBufferedBytes.Should().BeLessThan(IncrementalHtmlTokenizer.MaxBufferSize,
            $"alpha.21 sliding-window contract violated: peak buffered = {tok.PeakBufferedBytes:N0} B " +
            $"after consuming {tok.BytesConsumed:N0} B");
    }

    [Fact]
    public void IncrementalTokenizer_HoldsNoBytesBetweenDrainedFeeds()
    {
        // After every drained feed, in-flight bytes should be ~0 (or at most
        // the tail of an unclosed tag at the chunk boundary). The buffer must
        // not carry already-emitted history forward.
        var html = GenerateLargeHtml(megabytes: 1);
        var tok = new IncrementalHtmlTokenizer();

        int offset = 0;
        int maxResidual = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(ChunkSize, html.Length - offset);
            tok.Feed(html.AsSpan(offset, take));
            while (tok.TryReadTag(out _)) { }
            // _filled - _consumed isn't directly exposed; PeakBufferedBytes is
            // the closest signal. After drain, the only legitimate residual is
            // a partial tag straddling the boundary — bounded by tag length.
            // Use BytesConsumed delta vs total fed as the cheap cross-check.
            var consumedSoFar = tok.BytesConsumed;
            var fedSoFar = offset + take;
            var residual = (int)(fedSoFar - consumedSoFar);
            if (residual > maxResidual) maxResidual = residual;
            offset += take;
        }

        maxResidual.Should().BeLessThan(IncrementalHtmlTokenizer.MaxBufferSize,
            $"residual between fed and consumed should bound to O(longest tag); got {maxResidual:N0} B");
    }

    [Fact]
    public void IncrementalScanner_FivePlusMiB_HoldsBoundedBytes()
    {
        // Build a tiny synthetic template + drive a multi-MiB response through
        // the incremental scanner. Verdict here is whatever it is — we're only
        // measuring memory residency, not capture correctness.
        var template = BuildTrivialTemplate();
        var scanner = IncrementalFenceScanner.Create(template);

        var html = GenerateLargeHtml(megabytes: 5);
        int offset = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(ChunkSize, html.Length - offset);
            var v = scanner.Feed(html.AsSpan(offset, take));
            if (v is ScanVerdict.Captured or ScanVerdict.Bailout)
            {
                // Drain the remainder so PeakBufferedBytes covers the whole feed.
                offset += take;
                continue;
            }
            offset += take;
        }
        scanner.Flush();

        scanner.BytesConsumed.Should().BeGreaterThan(0);
        scanner.PeakBufferedBytes.Should().BeLessThan(IncrementalHtmlTokenizer.MaxBufferSize,
            $"scanner held {scanner.PeakBufferedBytes:N0} B vs response {html.Length:N0} B — sliding-window broken");
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
        // Tag-heavy body with classes and attributes — exercises ExtractClassHash
        // and keeps the tokenizer doing real work per chunk.
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
                // Sprinkle a script + comment to exercise the body-skip and comment paths.
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
        // Build any well-formed template — the scanner's per-tick memory bound
        // is independent of whether the fences match.
        var prefix = TemplateFence.BuildFromEvents(BuildTagEventsForNames("body", "header", "/header", "article"), requiredDepth: 0);
        var start = TemplateFence.BuildFromEvents(BuildTagEventsForNames("header", "/header", "article", "section"), requiredDepth: 0);
        var end = TemplateFence.BuildFromEvents(BuildTagEventsForNames("article", "/article", "footer", "/footer"), requiredDepth: 0);
        return new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "synthetic.test",
            PrefixFence = prefix,
            ContentStartFence = start,
            ContentEndFence = end,
            BailoutBytes = 10_000_000,
            MaxCaptureBytes = 10_000_000,
            WindowSize = 8,
            MaxEventsWithoutTransition = 1_000_000,
        };
    }

    private static (ulong tagHash, ulong classHash)[] BuildTagEventsForNames(params string[] names)
    {
        var result = new (ulong, ulong)[names.Length];
        Span<byte> buf = stackalloc byte[64];
        for (int i = 0; i < names.Length; i++)
        {
            var n = names[i];
            var name = n.StartsWith('/') ? n.AsSpan(1) : n.AsSpan();
            var len = Encoding.UTF8.GetBytes(name, buf);
            result[i] = (System.IO.Hashing.XxHash3.HashToUInt64(buf[..len]), 0UL);
        }
        return result;
    }
}
