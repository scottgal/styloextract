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
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));
        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(template);

        var selector = new StreamingPathSelector(store);

        ReadOnlySpan<byte> html =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;
        var result = selector.Scan(template.TemplateId, html);

        result.Should().Be(ScanVerdict.Captured);
    }

    [Fact]
    public async Task Selector_can_dispatch_two_distinct_templates_per_id()
    {
        var smallTemplate = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("nav"),
            TripwireTestHelpers.TagPattern("main"),
            TripwireTestHelpers.ClosePattern("main"));
        var bigTemplate = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(smallTemplate);
        await store.RegisterAsync(bigTemplate);

        var selector = new StreamingPathSelector(store);

        ReadOnlySpan<byte> smallHtml = "<body><nav></nav><main></main><footer></footer></body>"u8;
        selector.Scan(smallTemplate.TemplateId, smallHtml).Should().Be(ScanVerdict.Captured);

        ReadOnlySpan<byte> bigHtml =
            "<body><header>x</header><article>YES</article><footer>z</footer></body>"u8;
        selector.Scan(bigTemplate.TemplateId, bigHtml).Should().Be(ScanVerdict.Captured);
    }
}
