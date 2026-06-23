using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Playwright;
using StyloExtract.Abstractions;
using StyloExtract.Core;
using StyloExtract.Fingerprint;
using StyloExtract.Heuristics;
using StyloExtract.Html;
using StyloExtract.Markdown;
using StyloExtract.Playwright;
using StyloExtract.Templates;
using Xunit;

namespace StyloExtract.Playwright.Tests;

/// <summary>
/// Real-network worst-case for the Playwright path: BBC News. The site is heavy
/// JS, hydrates a lot of content post-load, runs bot detection on naive UAs,
/// and has a complex layout that exercises the classifier as much as the
/// renderer. If Playwright + the full extractor produce useful Markdown on
/// bbc.co.uk/news, the synthetic stubs are a floor not a ceiling.
///
/// <para>
/// SkippableFact: skipped when Chromium isn't installed OR the network is
/// unreachable. The test asserts on SHAPE (markdown length, heading count,
/// link count) not on specific article text, because the landing page changes
/// constantly.
/// </para>
/// </summary>
public class BbcNewsWorstCaseTests : IAsyncLifetime
{
    private bool _chromiumAvailable;

    public async Task InitializeAsync()
    {
        try
        {
            using var p = await Microsoft.Playwright.Playwright.CreateAsync();
            await using var b = await p.Chromium.LaunchAsync(new BrowserTypeLaunchOptions { Headless = true });
            _chromiumAvailable = true;
        }
        catch { _chromiumAvailable = false; }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private static (ILayoutExtractor extractor, SqliteConnection conn) Build()
    {
        var cs = $"Data Source=file:bbc-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
        var conn = new SqliteConnection(cs);
        conn.Open();
        SqliteSchema.EnsureCreated(conn);
        var index = new SqliteTemplateIndex(cs);
        var noise = ClassNoiseFilter.LoadFromEmbeddedResource();
        var sketcher = new MinHashSketcher(128);
        var fp = new StructuralFingerprinter(
            new ShingleGenerator(noise), sketcher, new LshBander(16, 8),
            new AnchorPathFingerprinter(noise, sketcher), new PqGramExtractor());
        return (new LayoutExtractor(
            new AngleSharpHtmlDomParser(), new DomCleaner(), fp,
            new BlockSegmenter(), HeuristicBlockClassifier.LoadFromEmbeddedResources(),
            new TypedMarkdownRenderer(), index, new HostHasher(new byte[32]),
            new ExtractorInducer(), new ExtractorApplicator(),
            fastPathThreshold: 0.85, slowPathThreshold: 0.75,
            new RefitOrchestrator(index, new ExtractorInducer(), 0.35, 5, 3),
            new DefaultNoopVersionEventSink()), conn);
    }

    [SkippableFact]
    public async Task Bbc_News_Landing_Page_Yields_Reader_Grade_Markdown_Through_Playwright()
    {
        Skip.IfNot(_chromiumAvailable, "Chromium browser not installed.");

        await using var fetcher = new PlaywrightHtmlFetcher();
        RenderedHtmlResult rendered;
        try
        {
            rendered = await fetcher.FetchAsync(
                new Uri("https://www.bbc.co.uk/news"),
                new RenderOptions
                {
                    // A real-browser UA. BBC's bot detection drops naive ones.
                    UserAgent = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
                                "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36",
                    NavigationTimeout = TimeSpan.FromSeconds(30),
                    WaitForNetworkIdleTimeout = TimeSpan.FromSeconds(8),
                });
        }
        catch (PlaywrightException)
        {
            Skip.If(true, "BBC News unreachable (Playwright navigation error). Network-dependent test.");
            return;
        }
        catch (TimeoutException)
        {
            Skip.If(true, "BBC News navigation timed out.");
            return;
        }

        // Sanity: Playwright fetched something substantial.
        rendered.StatusCode.Should().BeInRange(200, 299);
        rendered.Html.Length.Should().BeGreaterThan(50_000,
            because: "BBC News is large; anything <50KB means JS didn't run or a bot wall blocked us");

        // Full extraction pipeline through the rendered DOM.
        var (extractor, conn) = Build();
        try
        {
            var result = await extractor.ExtractAsync(rendered.Html, rendered.FinalUri);

            // SHAPE assertions: the landing page changes constantly so we can't
            // assert text. What we CAN assert is the markdown is reader-grade:
            //   * substantial body (>5KB markdown)
            //   * at least a handful of headings emerged
            //   * at least a handful of real links survived
            //   * the literal "BBC" appears (brand banner / link)
            result.Markdown.Length.Should().BeGreaterThan(5_000,
                because: "BBC News landing page has many headlines; thin output means classification failed");

            var headingCount = result.Markdown.Split('\n').Count(l => l.StartsWith('#'));
            headingCount.Should().BeGreaterThan(3,
                because: "BBC News landing should yield at least a handful of section/article headings");

            // Count markdown links - rough proxy: '[' that's followed by ']('.
            int linkCount = 0;
            for (int i = 0; i < result.Markdown.Length - 2; i++)
            {
                if (result.Markdown[i] == '[' && result.Markdown.IndexOf("](", i, StringComparison.Ordinal) is int idx
                    && idx > i && idx - i < 200)
                    linkCount++;
            }
            linkCount.Should().BeGreaterThan(10,
                because: "BBC News landing page is link-dense; any link survival count means the walker preserved hrefs");

            result.Markdown.Should().Contain("BBC", because: "the brand string should appear somewhere on a BBC page");

            // At least one block was classified as MainContent (the article list).
            result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
        }
        finally { conn.Dispose(); }
    }

    [SkippableFact]
    public async Task Bbc_News_Plain_Fetch_Vs_Playwright_Compares_Sensibly()
    {
        Skip.IfNot(_chromiumAvailable, "Chromium browser not installed.");

        const string ua = "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) " +
                          "AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36";

        string plainHtml;
        using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd(ua);
            try { plainHtml = await http.GetStringAsync("https://www.bbc.co.uk/news"); }
            catch (Exception) { Skip.If(true, "BBC News unreachable for plain HTTP fetch."); return; }
        }

        await using var fetcher = new PlaywrightHtmlFetcher();
        RenderedHtmlResult rendered;
        try
        {
            rendered = await fetcher.FetchAsync(new Uri("https://www.bbc.co.uk/news"), new RenderOptions
            {
                UserAgent = ua,
                NavigationTimeout = TimeSpan.FromSeconds(30),
                WaitForNetworkIdleTimeout = TimeSpan.FromSeconds(8),
            });
        }
        catch { Skip.If(true, "BBC News unreachable for Playwright fetch."); return; }

        // Both fetches succeed. The Playwright version's HTML is typically
        // LONGER (post-hydration) but doesn't have to be — modern BBC SSRs
        // most of the content. What matters is both produce reader-grade
        // markdown through the same pipeline.
        var (extractor, conn) = Build();
        try
        {
            var fromPlain = await extractor.ExtractAsync(plainHtml, new Uri("https://www.bbc.co.uk/news"));
            var fromPw = await extractor.ExtractAsync(rendered.Html, rendered.FinalUri);

            // Sanity: both produce non-trivial markdown. If one returns ~nothing
            // and the other returns lots, the difference tells us where the JS
            // path matters. Today BBC's SSR is good enough that both should
            // produce substantial output.
            fromPlain.Markdown.Length.Should().BeGreaterThan(2_000);
            fromPw.Markdown.Length.Should().BeGreaterThan(2_000);
        }
        finally { conn.Dispose(); }
    }
}
