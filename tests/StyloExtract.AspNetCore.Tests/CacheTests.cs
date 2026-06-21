using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.Abstractions;
using StyloExtract.AspNetCore;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Tests.TestWebApp;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Tests for the IDistributedCache caching feature.
/// </summary>
public sealed class CacheTests : IDisposable
{
    private readonly IHost _cachedHost;
    private readonly HttpClient _cachedClient;

    public CacheTests()
    {
        _cachedHost = BuildCachedHost();
        _cachedHost.Start();
        _cachedClient = _cachedHost.GetTestClient();
    }

    public void Dispose()
    {
        _cachedClient.Dispose();
        _cachedHost.Dispose();
    }

    // -----------------------------------------------------------------------
    // Basic miss then hit
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_firstRequest_isMiss()
    {
        using var request = BuildMarkdownRequest("/html");
        var response = await _cachedClient.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        GetCacheStatus(response).Should().Be("miss");
    }

    [Fact]
    public async Task Cache_secondRequest_isHit_withSameBody()
    {
        // First request populates the cache.
        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await _cachedClient.SendAsync(req1);
        var body1 = await resp1.Content.ReadAsStringAsync();

        // Second request should be a cache hit.
        using var req2 = BuildMarkdownRequest("/html");
        var resp2 = await _cachedClient.SendAsync(req2);
        var body2 = await resp2.Content.ReadAsStringAsync();

        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        GetCacheStatus(resp2).Should().Be("hit");
        body2.Should().Be(body1);
    }

    // -----------------------------------------------------------------------
    // Profile isolation
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_differentProfiles_cachesSeparately()
    {
        using var hostWithProfiles = BuildCachedHost();
        hostWithProfiles.Start();
        using var client = hostWithProfiles.GetTestClient();

        // RagFull profile.
        using var req1 = new HttpRequestMessage(HttpMethod.Get, "/html?stylo_profile=RagFull");
        req1.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        var resp1 = await client.SendAsync(req1);
        GetCacheStatus(resp1).Should().Be("miss");

        // AgentNavigation profile - different cache slot.
        using var req2 = new HttpRequestMessage(HttpMethod.Get, "/html?stylo_profile=AgentNavigation");
        req2.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        var resp2 = await client.SendAsync(req2);
        GetCacheStatus(resp2).Should().Be("miss");

        // Now RagFull again - should be a hit.
        using var req3 = new HttpRequestMessage(HttpMethod.Get, "/html?stylo_profile=RagFull");
        req3.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        var resp3 = await client.SendAsync(req3);
        GetCacheStatus(resp3).Should().Be("hit");
    }

    // -----------------------------------------------------------------------
    // Override query param excluded from cache key
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_overrideQueryParam_excludedFromKey_sharesEntry()
    {
        // Use a fresh host so the cache is empty.
        using var host = BuildCachedHost();
        host.Start();
        using var client = host.GetTestClient();

        // First: populate via Accept header.
        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await client.SendAsync(req1);
        GetCacheStatus(resp1).Should().Be("miss");

        // Second: use ?format=markdown (override). Cache key should be the same because
        // the "format" param is excluded. Should be a hit.
        var resp2 = await client.GetAsync("/html?format=markdown");
        GetCacheStatus(resp2).Should().Be("hit");
    }

    // -----------------------------------------------------------------------
    // Different paths
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_differentPaths_cachesSeparately()
    {
        using var host = BuildCachedHost(extraRoutes: true);
        host.Start();
        using var client = host.GetTestClient();

        // Populate /html.
        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await client.SendAsync(req1);
        GetCacheStatus(resp1).Should().Be("miss");

        // /html2 is different path: separate cache slot.
        using var req2 = BuildMarkdownRequest("/html2");
        var resp2 = await client.SendAsync(req2);
        GetCacheStatus(resp2).Should().Be("miss");

        // /html again: hit.
        using var req3 = BuildMarkdownRequest("/html");
        var resp3 = await client.SendAsync(req3);
        GetCacheStatus(resp3).Should().Be("hit");
    }

    // -----------------------------------------------------------------------
    // ETag / If-None-Match
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_etagSet_onMissAndHit()
    {
        // Populate.
        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await _cachedClient.SendAsync(req1);
        resp1.Headers.ETag.Should().NotBeNull();
        var etag = resp1.Headers.ETag!.Tag;

        // Hit.
        using var req2 = BuildMarkdownRequest("/html");
        var resp2 = await _cachedClient.SendAsync(req2);
        resp2.Headers.ETag!.Tag.Should().Be(etag);
    }

    [Fact]
    public async Task Cache_ifNoneMatchMatches_returns304()
    {
        // Populate.
        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await _cachedClient.SendAsync(req1);
        var etag = resp1.Headers.ETag!.Tag;

        // Second request with If-None-Match.
        using var req2 = BuildMarkdownRequest("/html");
        req2.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var resp2 = await _cachedClient.SendAsync(req2);

        resp2.StatusCode.Should().Be(HttpStatusCode.NotModified);
        var body = await resp2.Content.ReadAsStringAsync();
        body.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Cache disabled
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_disabled_alwaysExtracts_noXStyloCacheHeader()
    {
        using var host = BuildHost(cacheEnabled: false);
        host.Start();
        using var client = host.GetTestClient();

        using var req1 = BuildMarkdownRequest("/html");
        var resp1 = await client.SendAsync(req1);
        resp1.Headers.Contains("X-Stylo-Cache").Should().BeFalse();

        using var req2 = BuildMarkdownRequest("/html");
        var resp2 = await client.SendAsync(req2);
        resp2.Headers.Contains("X-Stylo-Cache").Should().BeFalse();

        resp1.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        resp2.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    // -----------------------------------------------------------------------
    // Cache-Control header
    // -----------------------------------------------------------------------

    [Fact]
    public async Task Cache_cacheControlHeader_notPresent_byDefault()
    {
        using var req = BuildMarkdownRequest("/html");
        var response = await _cachedClient.SendAsync(req);

        // EmitCacheControlHeader defaults to false.
        response.Headers.CacheControl.Should().BeNull();
    }

    [Fact]
    public async Task Cache_cacheControlHeader_present_whenEnabled()
    {
        using var host = BuildHost(cacheEnabled: true, emitCacheControl: true);
        host.Start();
        using var client = host.GetTestClient();

        using var req = BuildMarkdownRequest("/html");
        var response = await client.SendAsync(req);

        response.Headers.CacheControl.Should().NotBeNull();
        response.Headers.CacheControl!.Public.Should().BeTrue();
        response.Headers.CacheControl.MaxAge.Should().NotBeNull();
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static HttpRequestMessage BuildMarkdownRequest(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        return request;
    }

    private static string? GetCacheStatus(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("X-Stylo-Cache", out var values))
            return null;
        return string.Join(",", values);
    }

    private static IHost BuildCachedHost(bool extraRoutes = false)
        => BuildHost(cacheEnabled: true, extraRoutes: extraRoutes);

    private static IHost BuildHost(
        bool cacheEnabled = true,
        bool emitCacheControl = false,
        bool extraRoutes = false)
    {
        return Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation(opts =>
                    {
                        opts.Cache.Enabled = cacheEnabled;
                        opts.Cache.AbsoluteExpiration = TimeSpan.FromMinutes(5);
                        opts.Cache.SlidingExpiration = TimeSpan.FromMinutes(2);
                        opts.Cache.EmitCacheControlHeader = emitCacheControl;
                        opts.Cache.EnableEtag = true;
                    });
                    services.AddRouting();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtractMarkdownNegotiation();
                    app.UseEndpoints(endpoints =>
                    {
                        endpoints.MapGet("/html", () => Results.Content(MarkdownMiddlewareFactory.SampleHtml, "text/html"));
                        if (extraRoutes)
                        {
                            endpoints.MapGet("/html2", () => Results.Content(MarkdownMiddlewareFactory.SampleHtml, "text/html"));
                        }
                    });
                });
            })
            .Build();
    }
}
