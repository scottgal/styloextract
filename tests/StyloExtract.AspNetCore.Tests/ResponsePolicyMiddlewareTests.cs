using System.Net;
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
/// Tests for ResponsePolicyMiddleware pipeline mechanics.
/// Uses lightweight spy/stub policies to isolate framework behavior from policy logic.
/// </summary>
public sealed class ResponsePolicyMiddlewareTests : IDisposable
{
    // --- Spy policy helpers ---

    private sealed class SpyPolicy : IResponsePolicy
    {
        public bool OnRequestCalled { get; private set; }
        public bool OnServeCalled { get; private set; }
        public bool OnProducedCalled { get; private set; }

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) { OnRequestCalled = true; return ValueTask.CompletedTask; }
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) { OnServeCalled = true; return ValueTask.CompletedTask; }
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx) { OnProducedCalled = true; return ValueTask.CompletedTask; }
    }

    private sealed class ShortCircuitPolicy : IResponsePolicy
    {
        private readonly string _responseBody;
        public ShortCircuitPolicy(string responseBody) => _responseBody = responseBody;

        public async ValueTask OnRequestAsync(ResponsePolicyContext ctx)
        {
            ctx.ShouldShortCircuit = true;
            await ValueTask.CompletedTask;
        }

        public async ValueTask OnServeAsync(ResponsePolicyContext ctx)
        {
            ctx.HttpContext.Response.ContentType = "text/plain";
            await ctx.HttpContext.Response.WriteAsync(_responseBody);
        }

        public ValueTask OnProducedAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
    }

    private sealed class RewritePolicy : IResponsePolicy
    {
        private readonly string _replacement;
        public RewritePolicy(string replacement) => _replacement = replacement;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx)
        {
            ctx.RewrittenBody = System.Text.Encoding.UTF8.GetBytes(_replacement);
            ctx.RewrittenContentType = "text/plain";
            return ValueTask.CompletedTask;
        }
    }

    private sealed class PrependPolicy : IResponsePolicy
    {
        private readonly string _prefix;
        public PrependPolicy(string prefix) => _prefix = prefix;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx)
        {
            if (ctx.BufferedBody is { Length: > 0 } body)
            {
                var existing = System.Text.Encoding.UTF8.GetString(body.Span);
                ctx.RewrittenBody = System.Text.Encoding.UTF8.GetBytes(_prefix + existing);
                ctx.RewrittenContentType = "text/plain";
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class VaryPolicy : IResponsePolicy
    {
        private readonly string _varyHeader;
        public VaryPolicy(string varyHeader) => _varyHeader = varyHeader;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx)
        {
            ctx.VaryBy.Add(_varyHeader);
            return ValueTask.CompletedTask;
        }
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
    }

    // --- Factory helpers ---

    private static (IHost Host, HttpClient Client) BuildHost(
        Action<ResponsePolicyOptions> configureOptions,
        Action<IEndpointRouteBuilder> configureRoutes)
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
                        configureOptions(opts);
                        return opts;
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseStyloExtract();
                    app.UseEndpoints(configureRoutes);
                });
            })
            .Build();

        host.Start();
        return (host, host.GetTestClient());
    }

    private readonly List<(IHost, HttpClient)> _hosts = new();

    private (IHost, HttpClient) CreateHost(
        Action<ResponsePolicyOptions> configureOptions,
        Action<IEndpointRouteBuilder> configureRoutes)
    {
        var pair = BuildHost(configureOptions, configureRoutes);
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

    // --- Tests ---

    [Fact]
    public async Task NoPolicy_SkipsBuffering_ReturnsHtmlUnchanged()
    {
        var (_, client) = CreateHost(
            opts => { /* no policies registered */ },
            routes => routes.MapGet("/no-policy", () => Results.Content("<h1>Hello</h1>", "text/html")));

        var response = await client.GetAsync("/no-policy");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
        response.Headers.Should().NotContainKey("X-Stylo-Cache");
    }

    [Fact]
    public async Task SinglePolicy_OnRequestAsync_Called()
    {
        var spy = new SpyPolicy();

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("spy", spy),
            routes => routes.MapGet("/endpoint", () => Results.Content("ok", "text/plain"))
                .WithResponsePolicy("spy"));

        await client.GetAsync("/endpoint");

        spy.OnRequestCalled.Should().BeTrue();
    }

    [Fact]
    public async Task SinglePolicy_OnProducedAsync_Called()
    {
        var spy = new SpyPolicy();

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("spy", spy),
            routes => routes.MapGet("/endpoint", () => Results.Content("ok", "text/plain"))
                .WithResponsePolicy("spy"));

        await client.GetAsync("/endpoint");

        spy.OnProducedCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ShouldShortCircuit_SkipsDownstream_ServesCustomResponse()
    {
        var policy = new ShortCircuitPolicy("short-circuited");

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("sc", policy),
            routes => routes.MapGet("/endpoint",
                    (HttpContext _) => throw new InvalidOperationException("downstream must not be called"))
                .WithResponsePolicy("sc"));

        var response = await client.GetAsync("/endpoint");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("short-circuited");
    }

    [Fact]
    public async Task RewrittenBody_IsDeliveredToClient()
    {
        var policy = new RewritePolicy("rewritten-content");

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("rw", policy),
            routes => routes.MapGet("/endpoint", () => Results.Content("original", "text/plain"))
                .WithResponsePolicy("rw"));

        var response = await client.GetAsync("/endpoint");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("rewritten-content");
    }

    [Fact]
    public async Task TwoPolicies_BothOnProducedAsync_BodyChains()
    {
        var first = new RewritePolicy("step1");
        var second = new PrependPolicy("step2:");

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("first", first).AddPolicy("second", second),
            routes => routes.MapGet("/endpoint", () => Results.Content("original", "text/plain"))
                .WithResponsePolicy("first")
                .WithResponsePolicy("second"));

        var response = await client.GetAsync("/endpoint");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("step2:step1");
    }

    [Fact]
    public async Task VaryBy_AccumulatedFromMultiplePolicies()
    {
        var p1 = new VaryPolicy("Accept");
        var p2 = new VaryPolicy("Accept-Encoding");

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("v1", p1).AddPolicy("v2", p2),
            routes => routes.MapGet("/endpoint", () => Results.Content("ok", "text/plain"))
                .WithResponsePolicy("v1")
                .WithResponsePolicy("v2"));

        var response = await client.GetAsync("/endpoint");

        var vary = response.Headers.Vary.ToList();
        vary.Should().Contain(v => v.Contains("Accept", StringComparison.OrdinalIgnoreCase));
        vary.Should().Contain(v => v.Contains("Accept-Encoding", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task UnknownPolicyName_SkippedWithNoException()
    {
        var (_, client) = CreateHost(
            opts => { /* "nonexistent" is not registered */ },
            routes => routes.MapGet("/endpoint", () => Results.Content("ok", "text/plain"))
                .WithResponsePolicy("nonexistent"));

        // Should not throw; response passes through normally.
        var response = await client.GetAsync("/endpoint");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("ok");
    }

    [Fact]
    public async Task DirectPolicyInstance_WorksWithoutRegistry()
    {
        var rewrite = new RewritePolicy("direct-instance");

        var (_, client) = CreateHost(
            opts => { /* no named policies */ },
            routes => routes.MapGet("/endpoint", () => Results.Content("original", "text/plain"))
                .WithResponsePolicy(rewrite));

        var response = await client.GetAsync("/endpoint");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().Be("direct-instance");
    }

    [Fact]
    public async Task DeduplicatedPolicyInstance_RunsOnce()
    {
        var spy = new SpyPolicy();
        int callCount = 0;
        var countingRewrite = new CountingRewritePolicy(() => ++callCount);

        var (_, client) = CreateHost(
            opts => opts.AddPolicy("same", countingRewrite),
            // Same policy added twice via name and instance: deduplication by reference should prevent double execution.
            routes => routes.MapGet("/endpoint", () => Results.Content("original", "text/plain"))
                .WithResponsePolicy("same")
                .WithResponsePolicy(countingRewrite));

        await client.GetAsync("/endpoint");

        callCount.Should().Be(1, "the same policy instance should run only once");
    }

    private sealed class CountingRewritePolicy : IResponsePolicy
    {
        private readonly Action _onProduce;
        public CountingRewritePolicy(Action onProduce) => _onProduce = onProduce;

        public ValueTask OnRequestAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnServeAsync(ResponsePolicyContext ctx) => ValueTask.CompletedTask;
        public ValueTask OnProducedAsync(ResponsePolicyContext ctx)
        {
            _onProduce();
            return ValueTask.CompletedTask;
        }
    }
}
