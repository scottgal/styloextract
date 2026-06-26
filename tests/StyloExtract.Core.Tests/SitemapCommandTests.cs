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
}
