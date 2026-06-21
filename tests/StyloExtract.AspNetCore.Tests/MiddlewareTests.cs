using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using StyloExtract.AspNetCore.Tests.TestWebApp;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

public sealed class MiddlewareTests : IDisposable
{
    private readonly MarkdownMiddlewareFactory _factory;
    private readonly HttpClient _client;

    public MiddlewareTests()
    {
        _factory = new MarkdownMiddlewareFactory();
        _client = _factory.Client;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task Get_WithHtmlAccept_ReturnsHtml()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_ReturnsMarkdown()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Get_WithMarkdownAccept_HasVaryHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Headers.Vary.Should().Contain("Accept");
    }

    [Fact]
    public async Task Get_NotFound_ResponseUntouched()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/notfound");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        // Status 404 is not in the allowed set (default {200}); response body is passed through.
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task Get_JsonEndpoint_ResponseUntouched()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        // application/json should not be converted to markdown.
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
    }

    [Fact]
    public async Task Get_WithProfileHeader_UsesRequestedProfile()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));
        request.Headers.Add("X-Stylo-Profile", "AgentNavigation");

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Get_WithProfileQueryString_UsesRequestedProfile()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html?stylo_profile=AgentNavigation");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");
    }

    [Fact]
    public async Task Get_MarkdownContentTypeIsUtf8()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Content.Headers.ContentType!.CharSet.Should().Be("utf-8");
    }
}
