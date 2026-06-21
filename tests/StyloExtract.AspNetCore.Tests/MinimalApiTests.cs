using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using StyloExtract.AspNetCore.Tests.TestWebApp;
using Xunit;

namespace StyloExtract.AspNetCore.Tests;

/// <summary>
/// Validates WithMarkdownNegotiation endpoint filter and StyloExtractResults.HtmlOrMarkdown.
/// </summary>
public sealed class MinimalApiTests : IDisposable
{
    private readonly MarkdownMinimalApiFactory _factory;
    private readonly HttpClient _client;

    public MinimalApiTests()
    {
        _factory = new MarkdownMinimalApiFactory();
        _client = _factory.Client;
    }

    public void Dispose() => _factory.Dispose();

    [Fact]
    public async Task WithMarkdownNegotiation_HtmlAccept_ReturnsHtml()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task WithMarkdownNegotiation_MarkdownAccept_ReturnsMarkdown()
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
    public async Task WithMarkdownNegotiation_MarkdownAccept_HasVaryHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/html");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Headers.Vary.Should().Contain("Accept");
    }

    [Fact]
    public async Task HtmlOrMarkdown_HtmlAccept_ReturnsHtml()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/htmlormarkdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    [Fact]
    public async Task HtmlOrMarkdown_MarkdownAccept_ReturnsMarkdown()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/htmlormarkdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/markdown");

        var body = await response.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task HtmlOrMarkdown_MarkdownAccept_HasVaryHeader()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/htmlormarkdown");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/markdown"));

        var response = await _client.SendAsync(request);

        response.Headers.Vary.Should().Contain("Accept");
    }
}
