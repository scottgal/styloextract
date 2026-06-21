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
using StyloExtract.AspNetCore.Policies;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Tests for CacheHintPolicy: Cache-Control, ETag computation, If-None-Match/304, and Vary.
/// </summary>
public sealed class CacheHintPolicyTests : IDisposable
{
    private readonly List<(IHost Host, HttpClient Client)> _hosts = new();

    private (IHost, HttpClient) CreateHost(CacheHintOptions opts, string responseBody = "hello world")
    {
        var policy = new CacheHintPolicy(opts);

        var host = Host.CreateDefaultBuilder()
            .ConfigureWebHostDefaults(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(services =>
                {
                    services.AddRouting();
                    services.AddSingleton<ResponsePolicyOptions>(_ =>
                    {
                        var o = new ResponsePolicyOptions();
                        o.AddPolicy("cache", policy);
                        return o;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(e =>
                        e.MapGet("/endpoint",
                            () => Results.Content(responseBody, "text/plain"))
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

    private static string ComputeExpectedEtag(string body)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var hash = SHA256.HashData(bytes);
        return "\"" + Convert.ToHexString(hash).ToLowerInvariant() + "\"";
    }

    [Fact]
    public async Task MaxAge_EmitsCorrectCacheControl_Public()
    {
        var (_, client) = CreateHost(new CacheHintOptions { MaxAge = TimeSpan.FromMinutes(5), Public = true });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("public");
        cc.Should().Contain("max-age=300");
    }

    [Fact]
    public async Task SharedMaxAge_EmitsSMaxAge()
    {
        var (_, client) = CreateHost(new CacheHintOptions { SharedMaxAge = TimeSpan.FromMinutes(10), Public = true });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("s-maxage=600");
    }

    [Fact]
    public async Task NoStore_OverridesAllOtherDirectives()
    {
        var (_, client) = CreateHost(new CacheHintOptions
        {
            NoStore = true,
            MaxAge = TimeSpan.FromMinutes(5),
            Public = true,
        });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Be("no-store");
        cc.Should().NotContain("max-age");
        cc.Should().NotContain("public");
    }

    [Fact]
    public async Task NoCache_EmitsNoCache()
    {
        var (_, client) = CreateHost(new CacheHintOptions { NoCache = true });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("no-cache");
    }

    [Fact]
    public async Task MustRevalidate_Emitted()
    {
        var (_, client) = CreateHost(new CacheHintOptions { MustRevalidate = true });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("must-revalidate");
    }

    [Fact]
    public async Task Private_EmitsPrivate()
    {
        var (_, client) = CreateHost(new CacheHintOptions { Public = false });

        var response = await client.GetAsync("/endpoint");

        var cc = response.Headers.CacheControl!.ToString();
        cc.Should().Contain("private");
        cc.Should().NotContain("public");
    }

    [Fact]
    public async Task ETag_ComputedFromBodySHA256_MatchesExpected()
    {
        const string body = "hello world";
        var (_, client) = CreateHost(new CacheHintOptions { EmitETag = true }, body);

        var response = await client.GetAsync("/endpoint");

        var etag = response.Headers.ETag!.Tag;
        var expected = ComputeExpectedEtag(body);
        etag.Should().Be(expected);
    }

    [Fact]
    public async Task ETag_SameBody_SameEtagAcrossRequests()
    {
        const string body = "stable content";
        var (_, client) = CreateHost(new CacheHintOptions { EmitETag = true }, body);

        var r1 = await client.GetAsync("/endpoint");
        var r2 = await client.GetAsync("/endpoint");

        r1.Headers.ETag!.Tag.Should().Be(r2.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task ETag_DifferentBodies_DifferentETags()
    {
        var (_, client1) = CreateHost(new CacheHintOptions { EmitETag = true }, "body-one");
        var (_, client2) = CreateHost(new CacheHintOptions { EmitETag = true }, "body-two");

        var r1 = await client1.GetAsync("/endpoint");
        var r2 = await client2.GetAsync("/endpoint");

        r1.Headers.ETag!.Tag.Should().NotBe(r2.Headers.ETag!.Tag);
    }

    [Fact]
    public async Task IfNoneMatch_Match_Returns304WithEmptyBody()
    {
        const string body = "cacheable content";
        var (_, client) = CreateHost(new CacheHintOptions { EmitETag = true, HonorIfNoneMatch = true }, body);

        // First request: get the ETag.
        var first = await client.GetAsync("/endpoint");
        var etag = first.Headers.ETag!.Tag;

        // Second request: send the ETag.
        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.IfNoneMatch.ParseAdd(etag);
        var second = await client.SendAsync(request);

        second.StatusCode.Should().Be(HttpStatusCode.NotModified);
        var secondBody = await second.Content.ReadAsByteArrayAsync();
        secondBody.Should().BeEmpty();
    }

    [Fact]
    public async Task IfNoneMatch_NoMatch_Returns200WithBody()
    {
        const string body = "some content";
        var (_, client) = CreateHost(new CacheHintOptions { EmitETag = true, HonorIfNoneMatch = true }, body);

        var request = new HttpRequestMessage(HttpMethod.Get, "/endpoint");
        request.Headers.IfNoneMatch.ParseAdd("\"00000000000000000000000000000000\"");
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var responseBody = await response.Content.ReadAsStringAsync();
        responseBody.Should().Be(body);
    }

    [Fact]
    public async Task EmitETag_False_NoETagHeader()
    {
        var (_, client) = CreateHost(new CacheHintOptions { EmitETag = false });

        var response = await client.GetAsync("/endpoint");

        response.Headers.ETag.Should().BeNull();
    }

    [Fact]
    public async Task Vary_OptionsVary_AppearsInResponseHeader()
    {
        var (_, client) = CreateHost(new CacheHintOptions
        {
            Vary = { "Accept-Encoding" },
        });

        var response = await client.GetAsync("/endpoint");

        var vary = string.Join(", ", response.Headers.Vary);
        vary.Should().Contain("Accept-Encoding");
    }
}
