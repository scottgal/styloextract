using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class StreamingPathSelectorTests
{
    [Fact]
    public void Returns_NoTemplate_when_id_unknown()
    {
        var store = new InMemoryStreamingTemplateStore();
        var selector = new StreamingPathSelector(store);

        ReadOnlySpan<byte> html = "<body></body>"u8;
        var result = selector.Scan(Guid.NewGuid(), html);

        result.Should().Be(ScanVerdict.NoTemplate);
    }

    [Fact]
    public void Drives_full_stack_to_Captured_when_template_matches_html()
    {
        var template = BuildBodyHeaderArticleFooterTemplate(out var templateId);
        var store = new InMemoryStreamingTemplateStore();
        store.Register(template);

        var selector = new StreamingPathSelector(store, windowSize: 4);

        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;
        var result = selector.Scan(templateId, html);

        result.Should().Be(ScanVerdict.Captured);
    }

    private static StreamingTemplate BuildBodyHeaderArticleFooterTemplate(out Guid templateId)
    {
        templateId = Guid.NewGuid();
        return new StreamingTemplate
        {
            TemplateId = templateId,
            PrefixFence = TemplateFence.BuildFromEvents(
                TagEvents("<body>", "<header>", "</header>", "<article>"),
                requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(
                TagEvents("<header>", "</header>", "<article>", "</article>"),
                requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(
                TagEvents("<article>", "</article>", "<footer>", "</footer>"),
                requiredDepth: 0),
            MinContentDepth = 0,
            BailoutBytes = 100_000,
            MaxCaptureBytes = 100_000,
        };
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
