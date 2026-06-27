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

        // Fence design: <header> ... </header> <main> <article> ... </article> </main> <footer>...
        // The tripwire scanner fires on the first <header> open (prefix), the first <article>
        // open (content-start), and the matching </article> close at depth baseline (content-end).
        var template = TripwireTestHelpers.MakeTemplate(
            TripwireTestHelpers.TagClaim("header"),
            TripwireTestHelpers.TagClaim("article"),
            TripwireTestHelpers.TagClaim("article"));

        var store = new InMemoryStreamingTemplateStore();
        await store.RegisterAsync(template);
        var selector = new StreamingPathSelector(store);

        var result = selector.Scan(template.TemplateId, html);

        result.Should().Be(ScanVerdict.Captured);
    }
}
