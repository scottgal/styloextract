using FluentAssertions;
using StyloExtract.Abstractions;
using Xunit;

namespace StyloExtract.Playwright.Tests;

/// <summary>
/// Decorator behaviour: the wrapped extractor runs first; the Playwright
/// fetcher is only invoked when the static result has &lt; 200 chars of
/// content-role text AND a source URI was supplied. Uses stub
/// implementations so we don't depend on Chromium being present here.
/// </summary>
public class RenderingLayoutExtractorTests
{
    private sealed class StubExtractor : ILayoutExtractor
    {
        public Func<string, Uri?, ExtractionOptions?, CancellationToken, Task<ExtractionResult>> Handler { get; set; } =
            (h, u, o, ct) => Task.FromResult(EmptyResult);

        public int Calls { get; private set; }
        public List<string> ReceivedHtml { get; } = new();

        public Task<ExtractionResult> ExtractAsync(string html, Uri? sourceUri = null, ExtractionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            ReceivedHtml.Add(html);
            return Handler(html, sourceUri, options, cancellationToken);
        }
    }

    private sealed class StubFetcher : IRenderedHtmlFetcher
    {
        public string RenderedHtml { get; set; } = "";
        public int Calls { get; private set; }
        public Exception? Throw { get; set; }
        public Task<RenderedHtmlResult> FetchAsync(Uri uri, RenderOptions? options = null, CancellationToken cancellationToken = default)
        {
            Calls++;
            if (Throw is not null) throw Throw;
            return Task.FromResult(new RenderedHtmlResult { Html = RenderedHtml, FinalUri = uri, StatusCode = 200, FetchTime = TimeSpan.FromMilliseconds(1) });
        }
    }

    private static readonly ExtractionResult EmptyResult = ResultWithContentText(0);
    private static ExtractionResult ResultWithContentText(int chars) => new()
    {
        SourceUri = null,
        Title = null,
        Markdown = chars > 0 ? new string('a', chars) : "",
        Blocks = chars > 0
            ? new[] { new ExtractedBlock
                {
                    Id = "b", Role = BlockRole.MainContent, Confidence = 0.9,
                    Text = new string('a', chars), Markdown = "", XPath = "/", TextLength = chars,
                    LinkDensity = 0, Links = Array.Empty<ExtractedLink>(),
                } }
            : Array.Empty<ExtractedBlock>(),
        Match = new LayoutMatch { TemplateId = null, TemplateVersion = 1, FingerprintHex = "", Status = MatchStatus.Novel, Similarity = 0, ObservationCount = 0, LatencyMatch = TimeSpan.Zero, LatencyTotal = TimeSpan.Zero },
        Stats = new ExtractionStats { BlockCount = chars > 0 ? 1 : 0, FingerprintShingleCount = 0, ParseTime = TimeSpan.Zero, FingerprintTime = TimeSpan.Zero, MatchTime = TimeSpan.Zero, RenderTime = TimeSpan.Zero },
    };

    [Fact]
    public async Task Does_Not_Fetch_When_Source_Uri_Is_Null()
    {
        var stub = new StubExtractor { Handler = (h, u, o, ct) => Task.FromResult(EmptyResult) };
        var fetcher = new StubFetcher();
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        var r = await sut.ExtractAsync("<html/>", sourceUri: null);

        stub.Calls.Should().Be(1, "static extractor runs once");
        fetcher.Calls.Should().Be(0, "no URL means no Playwright fetch");
    }

    [Fact]
    public async Task Does_Not_Fetch_When_Static_Result_Has_Sufficient_Content()
    {
        var stub = new StubExtractor { Handler = (h, u, o, ct) => Task.FromResult(ResultWithContentText(300)) };
        var fetcher = new StubFetcher();
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        await sut.ExtractAsync("<html/>", new Uri("https://example.com/"));

        stub.Calls.Should().Be(1);
        fetcher.Calls.Should().Be(0, "static produced 300 chars of content — above threshold, no re-render");
    }

    [Fact]
    public async Task Fetches_And_Re_Extracts_When_Static_Is_Catastrophic_And_Url_Present()
    {
        var firstCall = true;
        var stub = new StubExtractor
        {
            Handler = (h, u, o, ct) =>
            {
                if (firstCall)
                {
                    firstCall = false;
                    return Task.FromResult(EmptyResult); // static empty
                }
                return Task.FromResult(ResultWithContentText(500)); // rendered yields content
            },
        };
        var fetcher = new StubFetcher { RenderedHtml = "<html><body>rendered content</body></html>" };
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        var r = await sut.ExtractAsync("<html/>", new Uri("https://example.com/"));

        stub.Calls.Should().Be(2, "static + re-extract on rendered HTML");
        fetcher.Calls.Should().Be(1);
        r.Blocks.Should().HaveCount(1, "second extraction's blocks returned");
    }

    [Fact]
    public async Task Returns_Static_Result_When_Playwright_Fetch_Throws()
    {
        var stub = new StubExtractor { Handler = (h, u, o, ct) => Task.FromResult(EmptyResult) };
        var fetcher = new StubFetcher { Throw = new InvalidOperationException("chromium unavailable") };
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        var r = await sut.ExtractAsync("<html/>", new Uri("https://example.com/"));

        stub.Calls.Should().Be(1);
        fetcher.Calls.Should().Be(1, "fetcher was attempted");
        r.Should().Be(EmptyResult, "static result returned unchanged on Playwright failure");
    }

    [Fact]
    public async Task Returns_Static_Result_When_Rendered_HTML_Same_Length_As_Static()
    {
        var html = "<html><body>shell</body></html>";
        var stub = new StubExtractor { Handler = (h, u, o, ct) => Task.FromResult(EmptyResult) };
        var fetcher = new StubFetcher { RenderedHtml = html }; // no new content
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        var r = await sut.ExtractAsync(html, new Uri("https://example.com/"));

        stub.Calls.Should().Be(1, "skip the re-extract when render returned identical-length HTML");
        fetcher.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Returns_Static_Result_When_Rendered_Re_Extract_Yields_No_Improvement()
    {
        var stub = new StubExtractor { Handler = (h, u, o, ct) => Task.FromResult(EmptyResult) };
        var fetcher = new StubFetcher { RenderedHtml = "<html><body>different but still empty</body></html>" };
        var sut = new RenderingLayoutExtractor(stub, fetcher);

        var r = await sut.ExtractAsync("<html/>", new Uri("https://example.com/"));

        stub.Calls.Should().Be(2, "tried both static and rendered");
        fetcher.Calls.Should().Be(1);
        r.Should().Be(EmptyResult, "rendered yielded no more content → keep static");
    }
}
