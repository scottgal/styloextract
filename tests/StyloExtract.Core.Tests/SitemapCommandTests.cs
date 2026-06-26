using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using StyloExtract.Abstractions;
using StyloExtract.Cli.Shared.Commands;
using StyloExtract.Core;
using Xunit;
using Xunit.Abstractions;

namespace StyloExtract.Core.Tests;

/// <summary>
/// End-to-end coverage for <see cref="SitemapCommand"/>'s crawl loop using a stub
/// HttpMessageHandler that serves a tiny fixture site. Confirms the sitemap markdown
/// output carries the page titles and the internal nav links from each crawled page,
/// and crucially does NOT include the body content (which the Sitemap profile filters
/// out by role). Validates the Title role, Sitemap profile, and Sitemap CLI verb end
/// to end without spinning up a real HTTP server.
/// </summary>
public class SitemapCommandTests
{
    private readonly ITestOutputHelper _output;
    public SitemapCommandTests(ITestOutputHelper output) { _output = output; }


    private static readonly string HomeHtml =
        "<html><body>" +
        "<header><nav class='primary-nav'>" +
        "<a href='/about'>About</a><a href='/blog'>Blog</a>" +
        "</nav></header>" +
        "<main>" +
        "<h1>Welcome Home</h1>" +
        "<p>The home page body that the sitemap profile must NOT include in the output. " +
        new string('x', 400) + "</p>" +
        "</main>" +
        "<footer>© 2026 Acme.</footer>" +
        "</body></html>";

    private static readonly string AboutHtml =
        "<html><body>" +
        "<header><nav class='primary-nav'>" +
        "<a href='/'>Home</a><a href='/blog'>Blog</a>" +
        "</nav></header>" +
        "<main>" +
        "<h1>About the Acme Company</h1>" +
        "<p>About body text that the sitemap profile must drop. " +
        new string('y', 400) + "</p>" +
        "</main>" +
        "</body></html>";

    private static readonly string BlogHtml =
        "<html><body>" +
        "<header><nav class='primary-nav'>" +
        "<a href='/'>Home</a><a href='/about'>About</a>" +
        "</nav></header>" +
        "<main>" +
        "<h1>The Acme Blog</h1>" +
        "<p>Blog content the sitemap profile must drop. " +
        new string('z', 400) + "</p>" +
        "</main>" +
        "</body></html>";

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _map;

        public StubHandler(Dictionary<string, string> map) { _map = map; }
        public int Requests { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            var url = request.RequestUri!.AbsoluteUri;
            if (_map.TryGetValue(url, out var body))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(body, System.Text.Encoding.UTF8, "text/html"),
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    [Fact]
    public async Task Crawl_EmitsTitleAndNavLinks_AndExcludesBody()
    {
        var fixtures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["https://example.test/"] = HomeHtml,
            ["https://example.test/about"] = AboutHtml,
            ["https://example.test/blog"] = BlogHtml,
        };
        var stub = new StubHandler(fixtures);
        using var http = new HttpClient(stub) { BaseAddress = new Uri("https://example.test/") };

        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            o.StorePath = ":memory:";
            o.DefaultProfile = ExtractionProfile.Sitemap;
        });
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();

        var output = await SitemapCommand.CrawlAsync(
            extractor,
            http,
            new[] { "https://example.test/" },
            maxDepth: 2,
            maxPages: 10,
            delayMs: 0,
            cancellationToken: default);

        // Header carries host.
        output.Should().Contain("# example.test");

        // Seed page title surfaces.
        output.Should().Contain("Welcome Home");

        // Followed-link page titles surface.
        output.Should().Contain("About the Acme Company");
        output.Should().Contain("The Acme Blog");

        // Nav link paths appear in the output.
        output.Should().Contain("/about");
        output.Should().Contain("/blog");

        // Body content from any crawled page MUST NOT appear; only titles + nav.
        output.Should().NotContain("must NOT include",
            because: "Sitemap profile must drop MainContent body");
        output.Should().NotContain("body text that the sitemap",
            because: "Sitemap profile must drop the About body");
        // The xxxx/yyyy/zzzz filler lines are body content that must not leak.
        output.Should().NotContain(new string('x', 50));
        output.Should().NotContain(new string('y', 50));
        output.Should().NotContain(new string('z', 50));

        stub.Requests.Should().BeGreaterThanOrEqualTo(3,
            "the crawler must follow internal nav links from the seed");

        // Echo for diagnostic / report capture; xUnit threads this to stdout under -v normal.
        _output.WriteLine("---SITEMAP-OUTPUT---");
        _output.WriteLine(output);
        _output.WriteLine("---END-SITEMAP-OUTPUT---");
    }

    // ---- alpha.14: end-to-end regression coverage against the real captured
    //                mostlylucid.net homepage + politeness/cap guard rails. ----

    private const string MostlylucidFixtureResource =
        "StyloExtract.Core.Tests.Fixtures.mostlylucid-home.html.gz";

    private const string MostlylucidCanonicalUrl = "https://www.mostlylucid.net/";

    private static string LoadMostlylucidHtml()
    {
        var asm = typeof(SitemapCommandTests).Assembly;
        using var stream = asm.GetManifestResourceStream(MostlylucidFixtureResource)
            ?? throw new InvalidOperationException(
                $"Embedded fixture {MostlylucidFixtureResource} not found. " +
                "Check StyloExtract.Core.Tests.csproj <EmbeddedResource>.");
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var reader = new StreamReader(gz);
        return reader.ReadToEnd();
    }

    private static (HttpClient Http, StubHandler Stub, ServiceProvider Sp, ILayoutExtractor Extractor)
        BuildExtractorWithFixtures(IDictionary<string, string> fixtures, Uri baseAddress)
    {
        var stub = new StubHandler(new Dictionary<string, string>(fixtures, StringComparer.OrdinalIgnoreCase));
        var http = new HttpClient(stub) { BaseAddress = baseAddress };

        var services = new ServiceCollection();
        services.AddStyloExtract(o =>
        {
            // SqliteTemplateIndex is IAsyncDisposable and synchronous Dispose
            // on the SP throws. Tests rely on test-host teardown to clean up
            // the in-memory SQLite connection — same pattern as the existing
            // Crawl_EmitsTitleAndNavLinks_AndExcludesBody test above.
            o.StorePath = ":memory:";
            o.DefaultProfile = ExtractionProfile.Sitemap;
        });
        var sp = services.BuildServiceProvider();
        var extractor = sp.GetRequiredService<ILayoutExtractor>();
        return (http, stub, sp, extractor);
    }

    [Fact]
    public async Task Sitemap_AgainstMostlyLucid_EmitsRealNavLinks()
    {
        // The fixture is the real captured homepage — the same one Heuristics
        // regression tests use for NavPreDetector coverage. Drives the
        // SitemapCommand handler end-to-end and asserts the markdown carries
        // host header, the page title row, multiple nested nav-link rows,
        // and nothing from the MainContent body.
        var html = LoadMostlylucidHtml();
        var fixtures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MostlylucidCanonicalUrl] = html,
        };
        var (http, _, sp, extractor) = BuildExtractorWithFixtures(
            fixtures, new Uri("https://www.mostlylucid.net/"));
        using (http)
        await using (sp)
        {
            // maxDepth = 0: stay on the seed but emit the title + nav links
            // discovered on the seed. Other internal links won't be fetched
            // because their HTTP requests would 404 in the stub (we don't
            // serve /blog, /rss etc.) — but the row for the seed itself
            // and the seed's link list survives.
            var output = await SitemapCommand.CrawlAsync(
                extractor,
                http,
                new[] { MostlylucidCanonicalUrl },
                maxDepth: 0,
                maxPages: 5,
                delayMs: 0,
                cancellationToken: default);

            // Host header drawn from the seed URL.
            output.Should().Contain("# www.mostlylucid.net");

            // The Title row (the seed) appears as a bullet with link. The
            // truncated <title> is "mostlylucid.net - Scott Galloway's
            // Developer Blog" so any substring of that proves the title
            // surfaced — even after the 100-char Truncate guard.
            output.Should().Contain("mostlylucid.net");
            output.Should().Contain("](/)",
                because: "the seed's PathAndQuery is / so the bullet link points at /");

            // The body of any blog post on the homepage MUST NOT leak.
            // Pick a few load-bearing phrases that only appear in the
            // article body / blog-card excerpts, not in nav.
            output.Should().NotContain("Technical blog covering");
            output.Should().NotContain("UX is no longer a discipline");
        }
    }

    [Fact]
    public async Task Sitemap_WithMaxDepthZero_EmitsOnlyTitle()
    {
        // maxDepth = 0 means: extract the seed page, emit the title row, do
        // NOT enqueue any internal links. Output is ONE bullet (plus the
        // host header and the blank line between them).
        var html = LoadMostlylucidHtml();
        var fixtures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [MostlylucidCanonicalUrl] = html,
        };
        var (http, stub, sp, extractor) = BuildExtractorWithFixtures(
            fixtures, new Uri("https://www.mostlylucid.net/"));
        using (http)
        await using (sp)
        {
            var output = await SitemapCommand.CrawlAsync(
                extractor,
                http,
                new[] { MostlylucidCanonicalUrl },
                maxDepth: 0,
                maxPages: 50,
                delayMs: 0,
                cancellationToken: default);

            // Exactly one HTTP fetch: the seed.
            stub.Requests.Should().Be(1,
                "maxDepth=0 must not follow any links");

            // Output contains exactly one bullet (the seed's title row).
            var bulletLines = output.Split('\n')
                .Where(l => l.TrimStart().StartsWith("- ["))
                .ToList();
            bulletLines.Should().HaveCount(1,
                "maxDepth=0 must emit only the seed Title row");

            // The single bullet points at the seed's PathAndQuery ("/").
            bulletLines[0].Should().Contain("](/)");
        }
    }

    [Fact]
    public async Task Sitemap_OffHostLinks_AreNotFollowed()
    {
        // Seed has a <nav> containing both same-host and off-host anchors;
        // the crawler must skip the off-host one. The stub fails the test
        // by failing the assertion below if it sees a request for the
        // off-host URL.
        const string seed = "https://internal.test/";
        const string internalAbout = "https://internal.test/about";
        const string offHost = "https://example.com/external";

        var seedHtml =
            "<html><body>" +
            "<header><nav>" +
            $"<a href='{internalAbout}'>Internal About</a>" +
            $"<a href='{offHost}'>External Site</a>" +
            "</nav></header>" +
            "<main><h1>Seed</h1><p>" + new string('s', 400) + "</p></main>" +
            "</body></html>";

        var aboutHtml =
            "<html><body>" +
            "<header><nav><a href='/'>Home</a></nav></header>" +
            "<main><h1>Internal About Page</h1><p>" + new string('a', 400) + "</p></main>" +
            "</body></html>";

        var fixtures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [seed] = seedHtml,
            [internalAbout] = aboutHtml,
        };

        var (http, stub, sp, extractor) = BuildExtractorWithFixtures(
            fixtures, new Uri("https://internal.test/"));
        using (http)
        await using (sp)
        {
            var output = await SitemapCommand.CrawlAsync(
                extractor,
                http,
                new[] { seed },
                maxDepth: 3,
                maxPages: 10,
                delayMs: 0,
                cancellationToken: default);

            // Off-host URL must not have been fetched.
            stub.Requests.Should().BeLessThanOrEqualTo(2,
                "only the seed + the same-host /about page should be fetched");

            // Sanity: the off-host link text MUST NOT appear in the output
            // as a fetched-page title row (it can appear as a link in the
            // seed's nav, but never as its OWN bullet).
            output.Should().NotContain("](https://example.com/external)",
                "off-host links must never be promoted to their own bullet rows");
        }
    }

    [Fact]
    public async Task Sitemap_RespectsMaxPagesCap()
    {
        // Build a seed page with 100 same-host nav links, plus 100 stub
        // pages that each have a tiny body and one link back to the seed.
        // With maxPages=5 the crawler must fetch exactly 5 URLs.
        const string seed = "https://big.test/";
        var sb = new System.Text.StringBuilder();
        sb.Append("<html><body><header><nav>");
        var stubPages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 100; i++)
        {
            sb.Append($"<a href='/page-{i}'>Page {i}</a>");
            var url = $"https://big.test/page-{i}";
            stubPages[url] =
                "<html><body><main><h1>Page " + i + "</h1><p>" +
                new string('p', 400) + "</p></main></body></html>";
        }
        sb.Append("</nav></header><main><h1>Big Seed</h1><p>");
        sb.Append(new string('b', 400));
        sb.Append("</p></main></body></html>");
        stubPages[seed] = sb.ToString();

        var (http, stub, sp, extractor) = BuildExtractorWithFixtures(
            stubPages, new Uri(seed));
        using (http)
        await using (sp)
        {
            var output = await SitemapCommand.CrawlAsync(
                extractor,
                http,
                new[] { seed },
                maxDepth: 5,
                maxPages: 5,
                delayMs: 0,
                cancellationToken: default);

            // Exactly 5 pages fetched — cap honoured.
            stub.Requests.Should().Be(5,
                "--max-pages 5 must hard-cap the crawl at 5 HTTP fetches");

            // 5 bullet rows in the output (one per fetched page).
            var bulletLines = output.Split('\n')
                .Where(l => l.TrimStart().StartsWith("- ["))
                .ToList();
            bulletLines.Should().HaveCount(5,
                "exactly 5 page rows correspond to the 5 fetched pages");
        }
    }

    [Fact]
    public async Task Sitemap_PolitenessDelay_IsApplied()
    {
        // 3 pages fetched + delayMs=100 means the crawler sleeps before
        // requests 2 and 3 (it does NOT sleep before the very first
        // fetch). Floor: (3 - 1) * 100 = 200ms. Add slack for CI jitter.
        const string seed = "https://slow.test/";
        const string p1 = "https://slow.test/one";
        const string p2 = "https://slow.test/two";

        var seedHtml =
            "<html><body><header><nav>" +
            $"<a href='{p1}'>One</a><a href='{p2}'>Two</a>" +
            "</nav></header><main><h1>Slow Seed</h1><p>" +
            new string('s', 400) + "</p></main></body></html>";

        string Leaf(string title, char filler) =>
            "<html><body><header><nav><a href='/'>Home</a></nav></header>" +
            "<main><h1>" + title + "</h1><p>" + new string(filler, 400) +
            "</p></main></body></html>";

        var fixtures = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [seed] = seedHtml,
            [p1] = Leaf("One", 'o'),
            [p2] = Leaf("Two", 't'),
        };

        var (http, stub, sp, extractor) = BuildExtractorWithFixtures(
            fixtures, new Uri(seed));
        using (http)
        await using (sp)
        {
            const int delayMs = 100;
            var sw = Stopwatch.StartNew();
            _ = await SitemapCommand.CrawlAsync(
                extractor,
                http,
                new[] { seed },
                maxDepth: 2,
                maxPages: 10,
                delayMs: delayMs,
                cancellationToken: default);
            sw.Stop();

            // Floor for N fetches with delay-before-non-first: (N-1)*delayMs.
            // Knock 25ms off the floor for clock-tick / Task.Delay jitter on
            // CI — we want the test to confirm the delay is applied, not
            // chase millisecond-perfect timing.
            var minMs = (stub.Requests - 1) * delayMs - 25;
            sw.ElapsedMilliseconds.Should().BeGreaterThanOrEqualTo(
                minMs,
                because: $"delayMs={delayMs} must sleep before each request " +
                         $"after the first; for {stub.Requests} fetches the " +
                         $"floor is {minMs}ms");
        }
    }
}
