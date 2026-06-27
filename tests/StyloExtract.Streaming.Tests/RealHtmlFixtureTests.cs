using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class RealHtmlFixtureTests
{
    [Fact]
    public async Task Scans_real_article_fixture_to_Captured()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "article.html");
        File.Exists(path).Should().BeTrue("article fixture should be copied to output");
        var html = File.ReadAllBytes(path);

        // Task 13 byte-pattern shape: prefix=<header>, content-start=<article>,
        // content-end=</article>. Same semantic pattern as the Task 4 tripwire
        // model, expressed as byte literals.
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagPattern("header"),
            TripwireTestHelpers.TagPattern("article"),
            TripwireTestHelpers.ClosePattern("article"));

        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(template);
        var selector = new StreamingPathSelector(store);

        var result = selector.Scan(template.TemplateId, html);

        result.Should().Be(ScanVerdict.Captured);
    }
}
