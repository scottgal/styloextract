using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.AspNetCore.CacheHints;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Policies;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Tests covering v1.2 final code-review findings: cache hit path, 304 correctness,
/// chain termination, Vary deduplication, case-insensitive lookup, fluent builder,
/// CacheHintsAttribute, and MVC controller [ResponsePolicy] resolution.
/// </summary>
public sealed class ReviewFixTests : IDisposable
{
    private readonly List<(IHost Host, HttpClient Client)> _hosts = new();

    public void Dispose()
    {
        foreach (var (host, client) in _hosts)
        {
            client.Dispose();
            host.Dispose();
        }
    }

    // ---------------------------------------------------------------------------
    // Minimal HTML used by negotiation tests.
    // ---------------------------------------------------------------------------

    internal const string SampleHtmlForController = """
        <!DOCTYPE html>
        <html lang="en">
        <head><title>Review Fix Test</title></head>
        <body>
          <main>
            <article>
              <h1>Review Fix Article</h1>
              <p>Primary content paragraph for the review-fix test suite.</p>
              <p>A second paragraph ensures the extractor has enough signal.</p>
              <ul><li>Alpha</li><li>Beta</li><li>Gamma</li></ul>
            </article>
          </main>
        </body>
        </html>
        """;

    private const string SampleHtml = SampleHtmlForController;

    // ---------------------------------------------------------------------------
    // Critical #1: Cache hit path (OnServeAsync was dead code)
    // ---------------------------------------------------------------------------

    /// <summary>
    /// Registers MarkdownNegotiationPolicy with caching enabled and attaches it via
    /// WithResponsePolicy("md"). The second identical request must be served from cache
    /// (X-Stylo-Cache: hit) without going downstream again.
    /// This is the test that proves the OnServeAsync dead-code bug is fixed.
    /// </summary>
    [Fact]
    public async Task MarkdownPolicy_CacheEnabled_SecondRequest_HitsCache()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation(o =>
                    {
                        o.Cache.Enabled = true;
                        o.Cache.AbsoluteExpiration = TimeSpan.FromMinutes(5);
                        o.Cache.EnableEtag = true;
                    });
                    services.AddSingleton<ResponsePolicyOptions>(sp =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("md", sp.GetRequiredService<MarkdownNegotiationPolicy>());
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint", () => Results.Content(SampleHtml, "text/html"))
                            .WithResponsePolicy("md"));
                });
            })
            .Build();

        host.Start();
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        // First request: extraction runs, cache miss.
        var req1 = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req1.Headers.Accept.ParseAdd("text/markdown");
        var resp1 = await client.SendAsync(req1);

        resp1.StatusCode.Should().Be(HttpStatusCode.OK);
        resp1.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        GetHeader(resp1, "X-Stylo-Cache").Should().Be("miss");

        // Second request: same URL + Accept, should be a cache hit.
        var req2 = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req2.Headers.Accept.ParseAdd("text/markdown");
        var resp2 = await client.SendAsync(req2);

        resp2.StatusCode.Should().Be(HttpStatusCode.OK);
        resp2.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
        GetHeader(resp2, "X-Stylo-Cache").Should().Be("hit",
            "the second request must be served from the distributed cache without re-extracting");

        // Both responses must have the same body.
        var body1 = await resp1.Content.ReadAsStringAsync();
        var body2 = await resp2.Content.ReadAsStringAsync();
        body2.Should().Be(body1);
    }

    // ---------------------------------------------------------------------------
    // Critical #2: 304 must not emit Content-Length (RFC 7232 §4.1)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CacheHintPolicy_NotModified_DoesNotEmitContentLength()
    {
        var (_, client) = CreateCacheHintHost();

        // First: get the ETag.
        var first = await client.GetAsync("/endpoint");
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var etag = first.Headers.ETag!.Tag;

        // Second: conditional GET that should produce 304.
        var req = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var second = await client.SendAsync(req);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);

        // RFC 7232 §4.1: 304 responses must not carry a Content-Length derived from an empty body.
        // Check the raw HTTP headers (not HttpContent.Headers.ContentLength, which is synthesized by
        // the HttpClient from the body when the actual header is absent).
        second.Content.Headers.TryGetValues("Content-Length", out var clValues).Should().BeFalse(
            "a 304 response must not include Content-Length per RFC 7232 §4.1");

        // Body must be empty.
        var body = await second.Content.ReadAsByteArrayAsync();
        body.Should().BeEmpty();
    }

    [Fact]
    public async Task CacheHintPolicy_NotModified_BodyIsEmpty_StatusIs304()
    {
        var (_, client) = CreateCacheHintHost();

        var first = await client.GetAsync("/endpoint");
        var etag = first.Headers.ETag!.Tag;

        var req = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var second = await client.SendAsync(req);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        (await second.Content.ReadAsByteArrayAsync()).Should().BeEmpty();
    }

    // ---------------------------------------------------------------------------
    // Important #3: 304 stops the OnProducedAsync chain
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CacheHintPolicy_NotModified_StopsChain()
    {
        // Register CacheHintPolicy first, then a sentinel that records whether it ran.
        bool sentinelRan = false;
        var sentinel = new SentinelPolicy(() => sentinelRan = true);
        var cacheHints = new CacheHintPolicy(new CacheHintOptions
        {
            EmitETag = true,
            HonorIfNoneMatch = true,
        });

        var (_, client) = CreateTwoPolicyHost(cacheHints, "cache", sentinel, "sentinel");

        // First request: populate ETag.
        var first = await client.GetAsync("/endpoint");
        var etag = first.Headers.ETag!.Tag;
        sentinelRan = false; // Reset after first request.

        // Second request: 304 should fire. Sentinel must NOT run after chain is terminated.
        var req = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(etag));
        var second = await client.SendAsync(req);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        sentinelRan.Should().BeFalse(
            "when CacheHintPolicy sets State=Terminate on 304, subsequent policies must not run in OnProducedAsync");
    }

    // ---------------------------------------------------------------------------
    // Important #6: Vary header written exactly once (no duplicates)
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task Composition_VaryHeader_NoDuplication()
    {
        // MarkdownNegotiationPolicy adds "Accept" to VaryBy.
        // CacheHintPolicy with Vary = ["Accept-Encoding"] adds "Accept-Encoding".
        // Neither policy should write Vary directly; the middleware writes it once.
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
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        var req = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req.Headers.Accept.ParseAdd("text/markdown");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);

        // Collect ALL Vary header values (the header may appear once with comma-separated values,
        // or as multiple header entries; GetValues normalises across both).
        var varyValues = response.Headers.Vary.ToList();
        var varyString = string.Join(", ", varyValues);

        varyString.Should().ContainEquivalentOf("Accept",
            "MarkdownNegotiationPolicy contributes Accept to VaryBy");
        varyString.Should().ContainEquivalentOf("Accept-Encoding",
            "CacheHintPolicy options contribute Accept-Encoding to VaryBy");

        // No duplicates: split on comma, trim, count distinct.
        var parts = varyString
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var distinct = new HashSet<string>(parts, StringComparer.OrdinalIgnoreCase);
        parts.Length.Should().Be(distinct.Count,
            "Vary header values must not be duplicated");
    }

    // ---------------------------------------------------------------------------
    // Important #4: AddStyloExtract(Action<ResponsePolicyBuilder>) fluent builder
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task FluentBuilder_AddPolicy_ResolvesAndAppliesPolicy()
    {
        // Wire via the new overload: AddStyloExtract(Action<ResponsePolicyBuilder>).
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();
                    // New fluent path.
                    services.AddStyloExtract(b =>
                    {
                        b.AddPolicy("md", p => p.NegotiateMarkdown());
                        b.AddPolicy("cache", p => p.CacheHints(o =>
                        {
                            o.MaxAge = TimeSpan.FromMinutes(5);
                            o.EmitETag = true;
                        }));
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
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        // Markdown negotiation should activate.
        var req = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        req.Headers.Accept.ParseAdd("text/markdown");
        var response = await client.SendAsync(req);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown",
            "the fluent builder must wire MarkdownNegotiationPolicy correctly");

        // ETag must be present (CacheHintPolicy is also wired).
        response.Headers.ETag.Should().NotBeNull(
            "the fluent builder must wire CacheHintPolicy correctly");
    }

    // ---------------------------------------------------------------------------
    // Important #5: CacheHintsAttribute discovery via endpoint metadata
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task CacheHintsAttribute_AppliedToEndpoint_EmitsCacheControl()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ResponsePolicyOptions>();
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint", () => Results.Content("hello", "text/plain"))
                            .WithMetadata(new CacheHintsAttribute
                            {
                                MaxAgeSeconds = 60,
                                Public = true,
                                EmitETag = false,
                            }));
                });
            })
            .Build();

        host.Start();
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        var response = await client.GetAsync("/endpoint");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("public");
        cc.Should().Contain("max-age=60",
            "CacheHintsAttribute with MaxAgeSeconds=60 must produce max-age=60");
    }

    // ---------------------------------------------------------------------------
    // Important #8: Policy name lookup is case-insensitive
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task PolicyName_CaseInsensitive_Resolves()
    {
        int produceCount = 0;
        var policy = new CountingPolicy(() => produceCount++);

        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ResponsePolicyOptions>(_ =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("md", policy); // registered as lowercase
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                    {
                        // Look up using uppercase "MD"
                        e.MapGet("/upper", () => Results.Content("ok", "text/plain"))
                            .WithResponsePolicy("MD");

                        // Look up using mixed case "Md"
                        e.MapGet("/mixed", () => Results.Content("ok", "text/plain"))
                            .WithResponsePolicy("Md");
                    });
                });
            })
            .Build();

        host.Start();
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        await client.GetAsync("/upper");
        await client.GetAsync("/mixed");

        produceCount.Should().Be(2,
            "policy registered as 'md' must resolve when looked up as 'MD' or 'Md'");
    }

    // ---------------------------------------------------------------------------
    // Important #9: MVC controller [ResponsePolicy] resolution
    // ---------------------------------------------------------------------------

    [Fact]
    public async Task MvcController_ResponsePolicyAttribute_AppliesPolicy()
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddControllers();
                    services.AddStyloExtract(o => o.StorePath = ":memory:");
                    services.AddStyloExtractMarkdownNegotiation();
                    services.AddSingleton<ResponsePolicyOptions>(sp =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("md", sp.GetRequiredService<MarkdownNegotiationPolicy>());
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e => e.MapControllers());
                });
            })
            .Build();

        host.Start();
        var client = host.GetTestClient();
        _hosts.Add((host, client));

        // GET with Accept: text/markdown should receive Markdown.
        var mdReq = new HttpRequestMessage(HttpMethod.Get, "/api/review-policy/article");
        mdReq.Headers.Accept.ParseAdd("text/markdown");
        var mdResp = await client.SendAsync(mdReq);

        mdResp.StatusCode.Should().Be(HttpStatusCode.OK);
        mdResp.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown",
            "[ResponsePolicy(\"md\")] on an MVC action must activate MarkdownNegotiationPolicy");

        // GET with Accept: text/html should receive HTML.
        var htmlReq = new HttpRequestMessage(HttpMethod.Get, "/api/review-policy/article");
        htmlReq.Headers.Accept.ParseAdd("text/html");
        var htmlResp = await client.SendAsync(htmlReq);

        htmlResp.StatusCode.Should().Be(HttpStatusCode.OK);
        htmlResp.Content.Headers.ContentType!.MediaType.Should().Be("text/html",
            "when Accept is text/html, the policy must not convert to Markdown");
    }

    // ---------------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------------

    private static string? GetHeader(HttpResponseMessage response, string name)
    {
        if (!response.Headers.TryGetValues(name, out var vals))
            return null;
        return string.Join(",", vals);
    }

    private (IHost, HttpClient) CreateCacheHintHost(string responseBody = "cacheable content")
    {
        var policy = new CacheHintPolicy(new CacheHintOptions
        {
            EmitETag = true,
            HonorIfNoneMatch = true,
            MaxAge = TimeSpan.FromMinutes(5),
        });

        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ResponsePolicyOptions>(_ =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy("cache", policy);
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint", () => Results.Content(responseBody, "text/plain"))
                            .WithResponsePolicy("cache"));
                });
            })
            .Build();

        host.Start();
        var pair = (host, host.GetTestClient());
        _hosts.Add(pair);
        return pair;
    }

    private (IHost, HttpClient) CreateTwoPolicyHost(
        IResponsePolicy first, string firstName,
        IResponsePolicy second, string secondName)
    {
        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ResponsePolicyOptions>(_ =>
                    {
                        var opts = new ResponsePolicyOptions();
                        opts.AddPolicy(firstName, first);
                        opts.AddPolicy(secondName, second);
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint", () => Results.Content("body", "text/plain"))
                            .WithResponsePolicy(firstName)
                            .WithResponsePolicy(secondName));
                });
            })
            .Build();

        host.Start();
        var pair = (host, host.GetTestClient());
        _hosts.Add(pair);
        return pair;
    }

    private sealed class SentinelPolicy : IResponsePolicy
    {
        private readonly Action _onProduce;
        public SentinelPolicy(Action onProduce) => _onProduce = onProduce;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx)
        {
            _onProduce();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingPolicy : IResponsePolicy
    {
        private readonly Action _onProduce;
        public CountingPolicy(Action onProduce) => _onProduce = onProduce;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx)
        {
            _onProduce();
            return ValueTask.CompletedTask;
        }
    }
}

