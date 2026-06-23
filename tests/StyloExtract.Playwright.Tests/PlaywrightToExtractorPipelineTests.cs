using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
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
/// End-to-end: a JS-only SPA stub is fetched via PlaywrightHtmlFetcher, the
/// rendered HTML is fed into the full LayoutExtractor pipeline, and the
/// markdown output is asserted to contain the SPA-rendered content. This is
/// the path that closes the "Medium-shape SPAs return zero blocks" gap from
/// the realworld-fixtures baseline.
/// </summary>
public class PlaywrightToExtractorPipelineTests : IAsyncLifetime
{
    private IHost? _host;
    private string _baseUrl = "";
    private bool _chromiumAvailable;

    private const string ShopifyShapedSpaStub = """
        <!DOCTYPE html>
        <html>
        <head><title>Product Page Stub</title></head>
        <body>
          <div id="app">loading...</div>
          <script>
            // Mirrors the Allbirds / generic Shopify-theme shape: empty shell,
            // JS hydrates the product page. Class names are NOT in the
            // framework-content-class-hints catalog; this is the case the ML
            // model is designed for, and the case Playwright unblocks today
            // by letting the heuristic at least SEE the rendered content.
            document.addEventListener('DOMContentLoaded', () => {
              document.getElementById('app').innerHTML = [
                '<header><nav class="site-nav"><a href="/">Home</a><a href="/shop">Shop</a></nav></header>',
                '<main class="product-detail-root">',
                '  <h1 class="product__title">SPA-rendered product title</h1>',
                '  <div class="product-description-body">',
                '    <p>Long-form product description that the extractor should pick up as the main content. This paragraph is rendered by JS after the initial HTML loads, so a plain fetch sees nothing useful and Playwright closes the gap.</p>',
                '    <p>A second descriptive paragraph keeping the body above the 40-char quality gate the renderer enforces for block emission.</p>',
                '  </div>',
                '</main>',
                '<footer>© 2026 Test Shop</footer>'
              ].join('');
            });
          </script>
        </body>
        </html>
        """;

    public async Task InitializeAsync()
    {
        _host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseUrls("http://127.0.0.1:0");
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/", () => Results.Content(ShopifyShapedSpaStub, "text/html"));
                    });
                });
            })
            .Build();
        await _host.StartAsync();
        var server = _host.Services.GetRequiredService<Microsoft.AspNetCore.Hosting.Server.IServer>();
        var addresses = server.Features.Get<Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()!;
        _baseUrl = addresses.Addresses.First();

        _chromiumAvailable = await ChromiumAvailability.CheckAsync();
    }

    public async Task DisposeAsync()
    {
        if (_host is not null) await _host.StopAsync();
        _host?.Dispose();
    }

    private static (ILayoutExtractor extractor, SqliteConnection conn) Build()
    {
        var cs = $"Data Source=file:pw-pipeline-{Guid.NewGuid():N}?mode=memory&cache=shared&uri=true";
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
    public async Task Playwright_Fetched_Spa_Produces_Real_Markdown_From_Extractor()
    {
        Skip.IfNot(_chromiumAvailable, "Chromium browser not installed.");

        await using var fetcher = new PlaywrightHtmlFetcher();
        var rendered = await fetcher.FetchAsync(new Uri(_baseUrl));
        rendered.Html.Should().Contain("SPA-rendered product title"); // sanity: JS ran

        var (extractor, conn) = Build();
        try
        {
            var result = await extractor.ExtractAsync(
                rendered.Html, rendered.FinalUri);

            // The extractor's <main> path identifies the product-detail-root as
            // MainContent (semantic <main> tag wins regardless of class names).
            // The walker emits the heading and at least one paragraph.
            result.Markdown.Should().Contain("# SPA-rendered product title");
            result.Markdown.Should().Contain("Long-form product description");
            result.Blocks.Should().Contain(b => b.Role == BlockRole.MainContent);
        }
        finally { conn.Dispose(); }
    }
}
