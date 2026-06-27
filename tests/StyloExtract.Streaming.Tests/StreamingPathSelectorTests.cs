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
    public async Task Drives_full_stack_to_Captured_when_template_matches_html()
    {
        var template = BuildBodyHeaderArticleFooterTemplate(out var templateId);
        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(template);

        var selector = new StreamingPathSelector(store);

        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;
        var result = selector.Scan(templateId, html);

        result.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public async Task Selector_uses_each_template_WindowSize_not_a_global()
    {
        // Two templates with different fence sizes — both must scan to Captured.
        // alpha.21: tags must be structural; the scanner filters everything else.
        // Fences are arranged so depth returns to capture-start depth when
        // contentEnd matches (depth-aware capture-end requirement).
        var smallTemplate = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            Host = "",
            PrefixFence = TemplateFence.BuildFromEvents(TagEvents("<nav>", "</nav>", "<main>"), requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(TagEvents("</nav>", "<main>", "</main>"), requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(TagEvents("<footer>", "</footer>", "</body>"), requiredDepth: 0),
            BailoutBytes = 100_000,
            MaxCaptureBytes = 100_000,
            WindowSize = 3,
            MaxEventsWithoutTransition = 256,
        };
        var bigTemplate = BuildBodyHeaderArticleFooterTemplate(out var bigId);

        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(smallTemplate);
        await store.RegisterAsync(bigTemplate);

        var selector = new StreamingPathSelector(store);

        ReadOnlySpan<byte> smallHtml = "<body><nav></nav><main></main><footer></footer></body>"u8;
        selector.Scan(smallTemplate.TemplateId, smallHtml).Should().Be(ScanVerdict.Captured);

        ReadOnlySpan<byte> bigHtml =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;
        selector.Scan(bigId, bigHtml).Should().Be(ScanVerdict.Captured);
    }

    private static StreamingTemplate BuildBodyHeaderArticleFooterTemplate(out Guid templateId)
    {
        templateId = Guid.NewGuid();
        return new StreamingTemplate
        {
            TemplateId = templateId,
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
