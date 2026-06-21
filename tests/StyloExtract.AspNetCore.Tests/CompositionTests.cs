using System.Net;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.AspNetCore.CacheHints;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Policies;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Integration tests for multi-policy composition: ordering, Vary combining, ETag from rewritten body.
/// </summary>
public sealed class CompositionTests : IDisposable
{
    private readonly List<(IHost Host, HttpClient Client)> _hosts = new();

    // Minimal HTML for extraction tests.
    private const string SampleHtml = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Composition Test</title></head>
        <body>
          <main>
            <article>
              <h1>Composition Article</h1>
              <p>This content is used to verify that ETag is computed from Markdown, not from HTML.</p>
              <p>A second paragraph ensures the extractor has enough signal to produce output.</p>
              <ul><li>Item A</li><li>Item B</li></ul>
            </article>
          </main>
        </body>
        </html>
        """;

    private (IHost, HttpClient) CreateCompositionHost(bool markdownFirst)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();

                    services.AddSingleton<ResponsePolicyOptions>(sp =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("md", sp.GetRequiredService<MarkdownNegotiationPolicy>());
                        opts.AddPolicy("cache", new CacheHintPolicy(new CacheHintOptions
                        {
                            EmitETag = true,
                            HonorIfNoneMatch = true,
                            MaxAge = TimeSpan.FromMinutes(5),
                        }));
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                    {
                        if (markdownFirst)
                        {
                            e.MapGet("/endpoint", () => Results.Content(SampleHtml, "text/html"))
                                .WithResponsePolicy("md")
                                .WithResponsePolicy("cache");
                        }
                        else
                        {
                            e.MapGet("/endpoint", () => Results.Content(SampleHtml, "text/html"))
                                .WithResponsePolicy("cache")
                                .WithResponsePolicy("md");
                        }
                    });
                });
            })
            .Build();

        host.Start();
        var pair = (host, host.GetTestClient());
        _hosts.Add(pair);
        return pair;
    }

    private (IHost, HttpClient) CreateVaryCombinationHost()
    {
        // markdown adds "Accept" to VaryBy; cache adds "Accept-Encoding" via options.Vary.
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();

                    services.AddSingleton<ResponsePolicyOptions>(sp =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("md", sp.GetRequiredService<MarkdownNegotiationPolicy>());
                        opts.AddPolicy("cache", new CacheHintPolicy(new CacheHintOptions
                        {
                            EmitETag = false,
                            Vary = { "Accept-Encoding" },
                        }));
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint", () => Results.Content(SampleHtml, "text/html"))
                            .WithResponsePolicy("md")
                            .WithResponsePolicy("cache"));
                });
            })
            .Build();

        host.Start();
        var pair = (host, host.GetTestClient());
        _hosts.Add(pair);
        return pair;
    }

    public void Dispose()
    {
        foreach (var (host, client) in _hosts)
        {
            client.Dispose();
            host.Dispose();
        }
    }

    [Fact]
    public async Task MarkdownThenCache_EtagComputedFromMarkdownBody()
    {
        var (_, client) = CreateCompositionHost(markdownFirst: true);

        // Request with Accept: text/markdown so markdown policy activates.
        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.Accept.ParseAdd("text/markdown");

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        var markdownBody = await response.Content.ReadAsByteArrayAsync();
        var expectedEtag = "\"" + Convert.ToHexString(SHA256.HashData(markdownBody)).ToLowerInvariant() + "\"";

        var actualEtag = response.Headers.ETag!.Tag;
        actualEtag.Should().Be(expectedEtag, "ETag must be computed from the Markdown body, not the original HTML");
    }

    [Fact]
    public async Task MarkdownThenCache_SecondRequest_Returns304()
    {
        var (_, client) = CreateCompositionHost(markdownFirst: true);

        // First request: get ETag.
        var first = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        first.Headers.Accept.ParseAdd("text/markdown");
        var firstResponse = await client.SendAsync(first);
        var etag = firstResponse.Headers.ETag!.Tag;

        // Second request: conditional GET.
        var second = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        second.Headers.Accept.ParseAdd("text/markdown");
        second.Headers.IfNoneMatch.ParseAdd(etag);

        var secondResponse = await client.SendAsync(second);

        secondResponse.StatusCode.Should().Be(HttpStatusCode.NotModified);
        var body = await secondResponse.Content.ReadAsByteArrayAsync();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task CacheThenMarkdown_EtagFromHtmlBody()
    {
        // When cache runs BEFORE markdown, the body the cache sees is still HTML.
        var (_, client) = CreateCompositionHost(markdownFirst: false);

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.Accept.ParseAdd("text/markdown");

        var response = await client.SendAsync(request);

        // Response body is still markdown (produced by last policy).
        // But ETag was computed from the HTML body (what cache saw in its OnProducedAsync turn).
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        var markdownBody = await response.Content.ReadAsByteArrayAsync();
        var htmlBytes = Encoding.UTF8.GetBytes(SampleHtml);
        var htmlEtag = "\"" + Convert.ToHexString(SHA256.HashData(htmlBytes)).ToLowerInvariant() + "\"";
        var markdownEtag = "\"" + Convert.ToHexString(SHA256.HashData(markdownBody)).ToLowerInvariant() + "\"";

        var actualEtag = response.Headers.ETag!.Tag;
        // The ETag should NOT match the markdown ETag (order matters).
        actualEtag.Should().Be(htmlEtag, "when cache runs first it sees HTML; ETag is from HTML bytes");
        actualEtag.Should().NotBe(markdownEtag);
    }

    [Fact]
    public async Task TwoPolicies_VaryHeaders_Combined()
    {
        var (_, client) = CreateVaryCombinationHost();

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.Accept.ParseAdd("text/markdown");
        var response = await client.SendAsync(request);

        var vary = string.Join(", ", response.Headers.Vary);

        vary.Should().Contain("Accept", "markdown policy contributes Accept");
        vary.Should().Contain("Accept-Encoding", "cache policy options contribute Accept-Encoding");
    }

    [Fact]
    public async Task MarkdownThenCache_HtmlRequest_EtagFromHtmlBody()
    {
        // When Accept explicitly requests text/html, markdown policy is inactive.
        // Cache policy computes ETag from the HTML body.
        var (_, client) = CreateCompositionHost(markdownFirst: true);

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.Accept.ParseAdd("text/html");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");

        var htmlBody = await response.Content.ReadAsByteArrayAsync();
        var expectedEtag = "\"" + Convert.ToHexString(SHA256.HashData(htmlBody)).ToLowerInvariant() + "\"";

        response.Headers.ETag!.Tag.Should().Be(expectedEtag);
    }
}
