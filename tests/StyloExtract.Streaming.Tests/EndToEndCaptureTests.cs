using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class EndToEndCaptureTests
{
    [Fact]
    public void Tokenizer_drives_scanner_to_captured_for_known_template()
    {
        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;

        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "",
            PrefixFence = TemplateFence.BuildFromEvents(
                TagEvents("<body>", "<header>", "</header>", "<article>"),
                requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(
                TagEvents("<header>", "</header>", "<article>", "</article>"),
                requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(
                TagEvents("<article>", "</article>", "<footer>", "</footer>"),
                requiredDepth: 0),
            BailoutBytes = 100_000,
            MaxCaptureBytes = 100_000,
            WindowSize = 4,
            MaxEventsWithoutTransition = 256,
        };

        Span<uint> signature = stackalloc uint[128];
        Span<EventSlot> window = stackalloc EventSlot[4];
        var scanner = new FenceScanner(in template, signature, window);
        var tokenizer = new MinimalHtmlTokenizer(html);

        var verdict = ScanVerdict.Continue;
        while (verdict == ScanVerdict.Continue && tokenizer.TryReadTag(out var evt))
            verdict = scanner.Tick(in evt);

        verdict.Should().Be(ScanVerdict.Captured);
        scanner.State.Should().Be(FenceState.Captured);
    }

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
