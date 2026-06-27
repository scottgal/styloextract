using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class IncrementalHtmlTokenizerTests
{
    /// <summary>
    /// Drain every TagEvent the span-based MinimalHtmlTokenizer would emit for
    /// <paramref name="bytes"/>. The incremental tokenizer must produce the
    /// same sequence (hash, isClose, byte length) for any chunking of the same
    /// input — that's the cross-validation contract this test class exercises.
    /// </summary>
    private static List<TagEvent> DrainMinimal(byte[] bytes)
    {
        var events = new List<TagEvent>();
        var t = new MinimalHtmlTokenizer(bytes);
        while (t.TryReadTag(out var evt)) events.Add(evt);
        return events;
    }

    private static List<TagEvent> DrainIncremental(byte[] bytes, int chunkSize)
    {
        var events = new List<TagEvent>();
        var t = new IncrementalHtmlTokenizer();
        for (int i = 0; i < bytes.Length; i += chunkSize)
        {
            var len = Math.Min(chunkSize, bytes.Length - i);
            t.Feed(bytes.AsSpan(i, len));
            while (t.TryReadTag(out var evt)) events.Add(evt);
        }
        // Drain any final buffered events.
        while (t.TryReadTag(out var evt)) events.Add(evt);
        return events;
    }

    [Fact]
    public void Single_chunk_matches_minimal_tokenizer()
    {
        var html = "<html><body><header><nav>menu</nav></header><p>hi</p><p>there</p><footer>x</footer></body></html>"u8.ToArray();
        var expected = DrainMinimal(html);
        var actual = DrainIncremental(html, html.Length);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Sixtyfour_byte_chunks_match_minimal_tokenizer()
    {
        var html = "<html><body><header><nav>menu</nav></header><p>hi</p><p>there</p><footer>x</footer></body></html>"u8.ToArray();
        var expected = DrainMinimal(html);
        var actual = DrainIncremental(html, chunkSize: 64);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Single_byte_chunks_match_minimal_tokenizer()
    {
        // Hardest possible chunking — every byte arrives separately. Every tag
        // and every script body has to survive arbitrary mid-tag splits.
        var html = "<html><body><header><nav>menu</nav></header><p>hi</p><p>there</p><footer>x</footer></body></html>"u8.ToArray();
        var expected = DrainMinimal(html);
        var actual = DrainIncremental(html, chunkSize: 1);
        actual.Should().Equal(expected);
    }

    [Fact]
    public void Tag_split_across_chunks_emits_single_event()
    {
        var tokenizer = new IncrementalHtmlTokenizer();
        tokenizer.Feed("…<hea"u8);
        tokenizer.TryReadTag(out _).Should().BeFalse("tag is incomplete");
        tokenizer.Feed("der>…"u8);
        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.IsClose.Should().BeFalse();
        evt.TagNameHash.Should().Be(XxHash3.HashToUInt64("header"u8));
    }

    [Fact]
    public void Comment_split_across_chunks_does_not_emit_tag()
    {
        var tokenizer = new IncrementalHtmlTokenizer();
        tokenizer.Feed("<!-- nav <fa"u8);
        tokenizer.TryReadTag(out _).Should().BeFalse();
        tokenizer.Feed("ke> --><p>"u8);
        tokenizer.TryReadTag(out var evt).Should().BeTrue();
        evt.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
        evt.IsClose.Should().BeFalse();
    }

    [Fact]
    public void Script_body_split_across_chunks_skips_inner_tags()
    {
        var tokenizer = new IncrementalHtmlTokenizer();
        tokenizer.Feed("<script>al"u8);
        tokenizer.TryReadTag(out var openEvt).Should().BeTrue();
        openEvt.TagNameHash.Should().Be(XxHash3.HashToUInt64("script"u8));

        // Inner '<x>' must be ignored — body skipping has to survive the chunk gap.
        tokenizer.Feed("ert(\"<x>"u8);
        tokenizer.TryReadTag(out _).Should().BeFalse();
        tokenizer.Feed("\")</scr"u8);
        tokenizer.TryReadTag(out _).Should().BeFalse();
        tokenizer.Feed("ipt><p></p>"u8);

        tokenizer.TryReadTag(out var closeEvt).Should().BeTrue();
        closeEvt.TagNameHash.Should().Be(XxHash3.HashToUInt64("script"u8));
        closeEvt.IsClose.Should().BeTrue();

        tokenizer.TryReadTag(out var pOpen).Should().BeTrue();
        pOpen.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
        pOpen.IsClose.Should().BeFalse();

        tokenizer.TryReadTag(out var pClose).Should().BeTrue();
        pClose.TagNameHash.Should().Be(XxHash3.HashToUInt64("p"u8));
        pClose.IsClose.Should().BeTrue();
    }

    [Fact]
    public void Empty_feed_is_noop()
    {
        var t = new IncrementalHtmlTokenizer();
        t.Feed(ReadOnlySpan<byte>.Empty);
        t.TryReadTag(out _).Should().BeFalse();
    }

    [Fact]
    public void Bytes_consumed_grows_monotonically()
    {
        var t = new IncrementalHtmlTokenizer();
        t.Feed("<p>"u8);
        t.TryReadTag(out _);
        var afterP = t.BytesConsumed;
        afterP.Should().BeGreaterThan(0);

        t.Feed("</p>"u8);
        t.TryReadTag(out _);
        t.BytesConsumed.Should().BeGreaterThan(afterP);
    }

    [Fact]
    public void Buffer_overflow_with_no_progress_throws()
    {
        // Pathological input: 2 MiB of bytes with no '<' or '>' at all. The
        // tokenizer can't advance _consumed past a single tag (none exists)
        // so the buffer eventually hits MaxBufferSize without progress and
        // must surface the failure rather than silently dropping bytes.
        var t = new IncrementalHtmlTokenizer();
        var junk = new byte[IncrementalHtmlTokenizer.MaxBufferSize + 1024];
        for (int i = 0; i < junk.Length; i++) junk[i] = (byte)'a';

        // The tokenizer drains buffer-cleanly when no '<' appears (advancing
        // _consumed to _filled), so feed bytes that DO contain partial tags
        // it can't complete — long '<' run with no '>'.
        for (int i = 0; i < junk.Length; i += 3) junk[i] = (byte)'<';

        var act = () =>
        {
            // Feed in 64 KiB chunks until we either overflow or finish.
            const int step = 64 * 1024;
            for (int i = 0; i < junk.Length; i += step)
            {
                var len = Math.Min(step, junk.Length - i);
                t.Feed(junk.AsSpan(i, len));
                while (t.TryReadTag(out _)) { /* drain */ }
            }
        };
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Two_hundred_kb_body_in_sixteen_kb_chunks_keeps_peak_under_partial_tag_size()
    {
        // alpha.21 contract: PeakBufferedBytes reflects ONLY the longest
        // partial-tag straddling a chunk boundary, not the chunk size.
        // 16 KB chunks over a 200 KB realistic page should peak well under
        // 4 KB (the new MaxBufferSize cap). Often the peak is 0 — a chunk
        // boundary that lands inside a text segment leaves nothing buffered.
        var html = BuildLargeRealisticPage(targetBytes: 200_000);
        html.Length.Should().BeGreaterThan(150_000);

        var tok = new IncrementalHtmlTokenizer();
        const int chunkSize = 16 * 1024;
        int offset = 0;
        int events = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(chunkSize, html.Length - offset);
            tok.Feed(html.AsSpan(offset, take));
            while (tok.TryReadTag(out _)) events++;
            offset += take;
        }

        events.Should().BeGreaterThan(100, "the synthetic page is tag-heavy");
        Console.WriteLine($"[alpha.21] html={html.Length}B chunks=16KB events={events} " +
                          $"peak={tok.PeakBufferedBytes}B consumed={tok.BytesConsumed}B");
        tok.PeakBufferedBytes.Should().BeLessThan(IncrementalHtmlTokenizer.MaxBufferSize,
            $"alpha.21 partial-tag-only buffer should stay under {IncrementalHtmlTokenizer.MaxBufferSize:N0} B; " +
            $"got {tok.PeakBufferedBytes:N0} B after consuming {tok.BytesConsumed:N0} B");
        // Tighter assertion: at worst a single partial tag (a few hundred
        // bytes) straddles a boundary. 512 B is a generous ceiling that
        // demonstrates we're nowhere near chunk-size.
        tok.PeakBufferedBytes.Should().BeLessThan(512,
            $"alpha.21 should peak in low-hundreds-of-bytes (or zero); got {tok.PeakBufferedBytes:N0} B");
    }

    [Fact]
    public void Two_hundred_kb_body_with_many_chunk_boundaries_still_bounded()
    {
        // 1024-byte chunks across a 200 KB body — almost every chunk boundary
        // lands inside a tag or text. Peak should still stay bounded by the
        // longest single partial tag (low hundreds of bytes).
        var html = BuildLargeRealisticPage(targetBytes: 200_000);
        var tok = new IncrementalHtmlTokenizer();
        const int chunkSize = 1024;
        int offset = 0;
        int events = 0;
        while (offset < html.Length)
        {
            var take = Math.Min(chunkSize, html.Length - offset);
            tok.Feed(html.AsSpan(offset, take));
            while (tok.TryReadTag(out _)) events++;
            offset += take;
        }
        Console.WriteLine($"[alpha.21] html={html.Length}B chunks=1KB events={events} " +
                          $"peak={tok.PeakBufferedBytes}B consumed={tok.BytesConsumed}B");
        events.Should().BeGreaterThan(100);
        tok.PeakBufferedBytes.Should().BeLessThan(512,
            $"alpha.21 contract: peak bounded by O(longest tag), not chunk size; got {tok.PeakBufferedBytes:N0} B");
    }

    [Fact]
    public void Real_world_html_in_random_chunks_matches_minimal()
    {
        // Random-sized chunks, 64-1024 bytes, to fuzz the chunk-boundary logic.
        var html = BuildRealisticPage();
        var expected = DrainMinimal(html);

        var rnd = new Random(0xC0FFEE);
        var t = new IncrementalHtmlTokenizer();
        var actual = new List<TagEvent>();
        int pos = 0;
        while (pos < html.Length)
        {
            var n = Math.Min(rnd.Next(64, 1024), html.Length - pos);
            t.Feed(html.AsSpan(pos, n));
            while (t.TryReadTag(out var evt)) actual.Add(evt);
            pos += n;
        }
        while (t.TryReadTag(out var evt)) actual.Add(evt);

        actual.Should().Equal(expected);
    }

    private static byte[] BuildRealisticPage()
    {
        var sb = new StringBuilder();
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        sb.Append("<title>Test</title><style>.a { color: red; }</style>");
        sb.Append("<script>var x = \"<fake>\";</script></head>");
        sb.Append("<body><header><nav><ul>");
        for (int i = 0; i < 5; i++) sb.Append("<li><a href=\"/x\">Item</a></li>");
        sb.Append("</ul></nav></header>");
        sb.Append("<main><article>");
        for (int i = 0; i < 20; i++) sb.Append("<p>Lorem ipsum dolor sit amet.</p>");
        sb.Append("</article></main>");
        sb.Append("<!-- a comment with <fake-tag> inside --><footer>copyright</footer>");
        sb.Append("</body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    private static byte[] BuildLargeRealisticPage(int targetBytes)
    {
        var sb = new StringBuilder(targetBytes + 1024);
        sb.Append("<!DOCTYPE html><html><head><meta charset=\"utf-8\"/>");
        sb.Append("<title>Synthetic</title></head><body>");
        sb.Append("<header><nav><ul>");
        for (int i = 0; i < 10; i++) sb.Append("<li><a href=\"/x\">nav-link</a></li>");
        sb.Append("</ul></nav></header>");
        sb.Append("<main><article>");
        int para = 0;
        while (sb.Length < targetBytes)
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
        }
        sb.Append("</article></main><footer>copyright</footer></body></html>");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }
}
