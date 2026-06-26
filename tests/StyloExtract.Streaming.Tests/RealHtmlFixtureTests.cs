using System.IO.Hashing;
using System.Text;
using FluentAssertions;
using Xunit;

namespace StyloExtract.Streaming.Tests;

public sealed class RealHtmlFixtureTests
{
    [Fact]
    public void Scans_real_article_fixture_to_Captured()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "article.html");
        File.Exists(path).Should().BeTrue("article fixture should be copied to output");
        var html = File.ReadAllBytes(path);

        // Fence design for this fixture's structure (skeleton was inspected directly):
        //   ... </header> <main> <article> <h1> </h1> <p> </p> <p> </p> </article> </main> <footer> ...
        // 4-event window + 4-event fences placed to land the FSM through every transition.
        var template = new StreamingTemplate
        {
            TemplateId = Guid.NewGuid(),
            PrefixFence = TemplateFence.BuildFromEvents(
                TagEvents("</header>", "<main>", "<article>", "<h1>"),
                requiredDepth: 0),
            ContentStartFence = TemplateFence.BuildFromEvents(
                TagEvents("<article>", "<h1>", "</h1>", "<p>"),
                requiredDepth: 0),
            ContentEndFence = TemplateFence.BuildFromEvents(
                TagEvents("</p>", "<p>", "</p>", "</article>"),
                requiredDepth: 0),
            MinContentDepth = 0,
            BailoutBytes = 1_000_000,
            MaxCaptureBytes = 1_000_000,
        };

        var store = new InMemoryStreamingTemplateStore();
        store.Register(template);
        var selector = new StreamingPathSelector(store, windowSize: 4);

        var result = selector.Scan(template.TemplateId, html);

        result.Should().Be(ScanVerdict.Captured);
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
