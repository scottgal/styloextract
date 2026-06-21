using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using StyloExtract.AspNetCore;
using StyloExtract.AspNetCore.Markdown;
using StyloExtract.AspNetCore.Tests.TestWebApp;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Tests for the query-string Accept override feature (<c>AcceptOverrideQueryName</c>).
/// </summary>
public sealed class QueryOverrideTests : IDisposable
{
    private readonly IHost _host;
    private readonly HttpClient _client;

    public QueryOverrideTests()
    {
        _host = BuildHost();
        _host.Start();
        _client = _host.GetTestClient();
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }

    // -----------------------------------------------------------------------
    // Middleware path
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QueryOverride_markdown_triggersExtraction_withoutAcceptHeader()
    {
        // No Accept header at all, but ?format=markdown should trigger extraction.
        var response = await _client.GetAsync("/html?format=markdown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task QueryOverride_markdown_overrides_htmlAcceptHeader()
    {
        // Real Accept header says text/html, but ?format=markdown wins.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?format=markdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task QueryOverride_html_suppressesExtraction_whenAcceptIsMarkdown()
    {
        // Real Accept is text/markdown, but ?format=html overrides it.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?format=html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task QueryOverride_unknownValue_fallsBackToRealAcceptHeader()
    {
        // ?format=mystery is not in the mapping; the real Accept header governs.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?format=mystery");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task QueryOverride_disabled_queryParamIsIgnored()
    {
        // Build a host with AcceptOverrideQueryName = null.
        using var host = BuildHost(opts => opts.AcceptOverrideQueryName = null);
        host.Start();
        using var client = host.GetTestClient();

        // Accept: text/html explicitly, ?format=markdown present but override is disabled.
        // The real Accept header governs; text/html is preferred so no extraction happens.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?format=markdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task QueryOverride_emptyQueryName_queryParamIsIgnored()
    {
        // Build a host with AcceptOverrideQueryName = "".
        using var host = BuildHost(opts => opts.AcceptOverrideQueryName = string.Empty);
        host.Start();
        using var client = host.GetTestClient();

        // Accept: text/html explicitly, ?format=markdown present but override is disabled.
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?format=markdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task QueryOverride_customMapping_works()
    {
        // Build a host with a custom mapping that maps "raw" to "text/markdown".
        using var host = BuildHost(opts =>
        {
            opts.AcceptOverrideQueryName = "fmt";
            opts.AcceptOverrideMappings.Clear();
            opts.AcceptOverrideMappings["raw"] = "text/markdown";
        });
        host.Start();
        using var client = host.GetTestClient();

        var response = await client.GetAsync("/html?fmt=raw");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task QueryOverride_setsXStyloAcceptOverrideHeader_whenOverrideFires()
    {
        var response = await _client.GetAsync("/html?format=markdown");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.Should().ContainKey("X-Stylo-Accept-Override");
        response.Headers.GetValues("X-Stylo-Accept-Override").Should().Contain("text/markdown");
    }

    [Fact]
    public async Task QueryOverride_noOverrideHeader_whenQueryParamAbsent()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Headers.Contains("X-Stylo-Accept-Override").Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // md alias
    // -----------------------------------------------------------------------

    [Fact]
    public async Task QueryOverride_mdAlias_triggersExtraction()
    {
        var response = await _client.GetAsync("/html?format=md");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    private static IHost BuildHost(Action<MarkdownNegotiationOptions>? configure = null)
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
                        configure?.Invoke(opts);
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
                    });
                });
            })
            .Build();
    }
}
